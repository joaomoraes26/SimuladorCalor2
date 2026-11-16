using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MySqlConnector;

namespace SimuladorCalor
{
    // Grava a simulacao no MySQL em SEGUNDO PLANO, no modelo produtor/consumidor:
    //
    //   Tela (produtor)                Fila (Channel)            Thread do banco (consumidor)
    //   tira a "foto" dos cubos  --->  [it 5][it 6][it 7]  --->  INSERTs em lote + COMMIT
    //
    // A tela nao espera o banco (so se a fila passar do limite), e a conexao MySQL e usada
    // por UMA unica thread, porque MySqlConnection nao pode ser compartilhada entre threads.
    //
    // Comandos SQL usados: INSERT, SELECT e UPDATE (nenhum DELETE).
    public sealed class GravadorBanco
    {
        private readonly string stringConexao;
        private readonly Channel<ComandoBanco> fila;
        private readonly Task tarefaConsumidora;

        private int pendentes;              // comandos na fila ainda nao gravados (Interlocked: usado pelas duas threads)
        private long iteracoesGravadas;     // contador para mostrar na tela
        private long idSimulacaoAtual = -1; // gerado pelo banco (AUTO_INCREMENT)
        private volatile string? erro;      // primeira falha do banco; depois dela a gravacao para

        // nome do material -> id_material. Usado SO pela thread do banco, por isso nao precisa de sincronizacao
        private readonly Dictionary<string, int> idsMateriais = new Dictionary<string, int>();

        public int Pendentes => Volatile.Read(ref pendentes);
        public long IteracoesGravadas => Interlocked.Read(ref iteracoesGravadas);
        public long IdSimulacaoAtual => Interlocked.Read(ref idSimulacaoAtual);
        public string? Erro => erro;

        public GravadorBanco(string stringConexao)
        {
            this.stringConexao = stringConexao;
            fila = Channel.CreateUnbounded<ComandoBanco>(new UnboundedChannelOptions
            {
                SingleReader = true, // so a thread do banco le
                SingleWriter = true  // so a tela escreve
            });
            tarefaConsumidora = Task.Run(ProcessarFilaAsync); // inicia o consumidor em uma thread do pool
        }

        // Testa a conexao antes de comecar, para avisar o usuario na hora. Retorna null se deu certo.
        public static string? TestarConexao(string stringConexao)
        {
            try
            {
                using var conexao = new MySqlConnection(stringConexao);
                conexao.Open();
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        // Chamado pela tela: so coloca o comando na fila e retorna na hora
        public bool Enviar(ComandoBanco comando)
        {
            if (erro != null) return false; // o banco ja falhou: nao adianta enfileirar mais nada

            Interlocked.Increment(ref pendentes); // conta ANTES de escrever, senao o consumidor poderia decrementar primeiro e o contador ficaria negativo
            if (!fila.Writer.TryWrite(comando)) // fila ilimitada: so falha se ja foi fechada pelo Encerrar
            {
                Interlocked.Decrement(ref pendentes); // desfaz a contagem, o comando nao entrou
                return false;
            }
            return true;
        }

        // Fecha a fila e espera gravar o que falta. Retorna true se tudo foi gravado.
        public bool Encerrar(TimeSpan tempoMaximo)
        {
            fila.Writer.TryComplete(); // avisa o consumidor que nao vem mais nada: ele grava o que falta e sai do await foreach
            bool terminou = tarefaConsumidora.Wait(tempoMaximo); // trava a tela ate terminar (ou ate o tempo maximo)
            return terminou && erro == null && Pendentes == 0;
        }

        private async Task ProcessarFilaAsync()
        {
            try
            {
                await using var conexao = new MySqlConnection(stringConexao); // UMA conexao, usada so por esta thread do inicio ao fim
                await conexao.OpenAsync();
                await CarregarMateriaisAsync(conexao); // descobre o id_material de cada nome

                // ReadAllAsync espera (sem gastar CPU) chegar um comando, e termina quando a fila e fechada
                await foreach (ComandoBanco comando in fila.Reader.ReadAllAsync())
                {
                    switch (comando) // o tipo do record diz o que fazer (pattern matching)
                    {
                        case CmdNovaSimulacao nova:
                            await GravarNovaSimulacaoAsync(conexao, nova);
                            break;
                        case CmdSalvarIteracao salvar:
                            await GravarIteracaoAsync(conexao, salvar.Iteracao);
                            break;
                        case CmdFinalizarSimulacao fim:
                            await FinalizarSimulacaoAsync(conexao, fim);
                            break;
                    }
                    Interlocked.Decrement(ref pendentes); // gravou: um a menos na fila (a tela usa isso para saber se precisa esperar)
                }
            }
            catch (Exception ex)
            {
                erro = ex.Message; // a tela verifica esta propriedade e avisa o usuario
            }
        }

        // SELECT: le os ids dos materiais cadastrados pelo script SQL
        private async Task CarregarMateriaisAsync(MySqlConnection conexao)
        {
            using (var cmd = new MySqlCommand("SELECT id_material, nome FROM material", conexao))
            await using (MySqlDataReader leitor = await cmd.ExecuteReaderAsync())
            {
                while (await leitor.ReadAsync())
                    idsMateriais[leitor.GetString(1)] = leitor.GetInt32(0);
            }

            foreach (Material m in Material.Todos)
            {
                if (!idsMateriais.ContainsKey(m.Nome))
                    throw new InvalidOperationException(
                        $"O material '{m.Nome}' nao esta cadastrado na tabela material. Execute o script simulador_calor.sql.");
            }
        }

        // INSERT na simulacao + INSERT dos corpos da iteracao 0 (estado inicial)
        private async Task GravarNovaSimulacaoAsync(MySqlConnection conexao, CmdNovaSimulacao nova)
        {
            using (var cmd = new MySqlCommand(
                "INSERT INTO simulacao (tamanho_n, dt_s, temperaturas_aleatorias, processamento_paralelo, " +
                "quantidade_blocos, status, data_inicio) " +
                "VALUES (@n, @dt, @aleatorias, @paralelo, @blocos, @status, @inicio)", conexao))
            {
                cmd.Parameters.AddWithValue("@n", nova.N);
                cmd.Parameters.AddWithValue("@dt", nova.Dt);
                cmd.Parameters.AddWithValue("@aleatorias", nova.TemperaturasAleatorias);
                cmd.Parameters.AddWithValue("@paralelo", nova.ProcessamentoParalelo);
                cmd.Parameters.AddWithValue("@blocos", nova.QuantidadeBlocos);
                cmd.Parameters.AddWithValue("@status", StatusSimulacao.EM_ANDAMENTO.ToString());
                cmd.Parameters.AddWithValue("@inicio", nova.DataInicio);
                await cmd.ExecuteNonQueryAsync();
                Interlocked.Exchange(ref idSimulacaoAtual, cmd.LastInsertedId); // id gerado pelo AUTO_INCREMENT
            }

            await GravarIteracaoAsync(conexao, nova.Inicial);
        }

        // INSERT de todos os corpos de uma iteracao, em lotes, dentro de UMA transacao:
        // ou a iteracao inteira e gravada, ou nada (nunca fica uma iteracao pela metade)
        private async Task GravarIteracaoAsync(MySqlConnection conexao, IteracaoRegistro iteracao)
        {
            long idSimulacao = IdSimulacaoAtual;
            if (idSimulacao < 0)
                throw new InvalidOperationException("Iteracao recebida sem simulacao aberta.");

            EstadoCorpoRegistro[] corpos = iteracao.Corpos;

            await using MySqlTransaction transacao = await conexao.BeginTransactionAsync();

            for (int inicio = 0; inicio < corpos.Length; inicio += ConfiguracaoBanco.CorposPorInsert)
            {
                int fim = Math.Min(inicio + ConfiguracaoBanco.CorposPorInsert, corpos.Length);

                // monta: INSERT INTO corpo (...) VALUES (@sim,@it,@t,...),(@sim,@it,@t,...),...
                var sql = new StringBuilder(
                    "INSERT INTO corpo (id_simulacao, id_material, iteracao, tempo_simulado_s, linha, coluna, " +
                    "lado_m, temperatura_k, massa_kg, calor_sensivel_j) VALUES ");

                using var cmd = new MySqlCommand { Connection = conexao, Transaction = transacao };
                cmd.Parameters.AddWithValue("@sim", idSimulacao);        // iguais para todas as linhas do lote
                cmd.Parameters.AddWithValue("@it", iteracao.Numero);
                cmd.Parameters.AddWithValue("@t", iteracao.TempoSimulado);

                for (int k = inicio; k < fim; k++)
                {
                    EstadoCorpoRegistro c = corpos[k];
                    int idMaterial = idsMateriais[c.Material];

                    if (k > inicio) sql.Append(',');
                    sql.Append($"(@sim,@m{k},@it,@t,@l{k},@c{k},@lado{k},@temp{k},@massa{k},@q{k})");

                    cmd.Parameters.AddWithValue($"@m{k}", idMaterial);
                    cmd.Parameters.AddWithValue($"@l{k}", c.Linha);
                    cmd.Parameters.AddWithValue($"@c{k}", c.Coluna);
                    cmd.Parameters.AddWithValue($"@lado{k}", c.Lado);
                    cmd.Parameters.AddWithValue($"@temp{k}", c.Temperatura);
                    cmd.Parameters.AddWithValue($"@massa{k}", c.Massa);
                    cmd.Parameters.AddWithValue($"@q{k}", c.CalorSensivel);
                }

                cmd.CommandText = sql.ToString();
                await cmd.ExecuteNonQueryAsync();
            }

            await transacao.CommitAsync();
            Interlocked.Increment(ref iteracoesGravadas);
        }

        // UPDATE: fecha a simulacao com o resultado final
        private async Task FinalizarSimulacaoAsync(MySqlConnection conexao, CmdFinalizarSimulacao fim)
        {
            if (IdSimulacaoAtual < 0) return;

            using var cmd = new MySqlCommand(
                "UPDATE simulacao SET status = @status, data_fim = @fim, tempo_equilibrio_s = @equilibrio, " +
                "tempo_processamento_ms = @processamento WHERE id_simulacao = @sim", conexao);
            cmd.Parameters.AddWithValue("@status", fim.Status.ToString());
            cmd.Parameters.AddWithValue("@fim", fim.DataFim);
            cmd.Parameters.AddWithValue("@equilibrio", (object?)fim.TempoEquilibrio ?? DBNull.Value); // NULL se nao chegou ao equilibrio
            cmd.Parameters.AddWithValue("@processamento", fim.TempoProcessamentoMs);
            cmd.Parameters.AddWithValue("@sim", IdSimulacaoAtual);
            await cmd.ExecuteNonQueryAsync();

            Interlocked.Exchange(ref idSimulacaoAtual, -1);
        }
    }
}

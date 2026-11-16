using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MySqlConnector;

namespace SimuladorCalor
{
    // Le do MySQL o que o GravadorBanco gravou, para a REPRISE.
    // So usa SELECT: nada e alterado no banco e nenhuma conta de troca de calor e refeita,
    // os numeros mostrados na tela sao exatamente os que foram calculados na simulacao original.
    //
    // Os metodos sao chamados um de cada vez (a tela so pede a proxima iteracao depois que a anterior chegou),
    // porque uma MySqlConnection nao aceita duas consultas ao mesmo tempo.
    public sealed class LeitorBanco : IAsyncDisposable
    {
        private readonly MySqlConnection conexao;

        // id_material -> nome. Carregado uma vez so; assim o SELECT dos corpos nao precisa de JOIN
        // e o mesmo texto do nome e reaproveitado nas 40.000 linhas de uma matriz 200x200
        private readonly Dictionary<int, string> nomesMateriais = new Dictionary<int, string>();

        private LeitorBanco(MySqlConnection conexao) // privado: quem cria e o AbrirAsync, que ja deixa a conexao pronta
        {
            this.conexao = conexao;
        }

        // Abre a conexao e le a tabela material. Se o servidor estiver desligado, a excecao sobe para a tela avisar
        public static async Task<LeitorBanco> AbrirAsync(string stringConexao)
        {
            var conexao = new MySqlConnection(stringConexao);
            try
            {
                // ConfigureAwait(false): a continuacao roda em uma thread do pool, e nao na thread da tela.
                // Assim, montar as 40.000 linhas de uma iteracao grande nao trava a janela
                await conexao.OpenAsync().ConfigureAwait(false);
                var leitor = new LeitorBanco(conexao);
                await leitor.CarregarMateriaisAsync().ConfigureAwait(false);
                return leitor;
            }
            catch
            {
                await conexao.DisposeAsync().ConfigureAwait(false); // deu erro: fecha a conexao antes de repassar o erro
                throw;
            }
        }

        private async Task CarregarMateriaisAsync()
        {
            using var cmd = new MySqlCommand("SELECT id_material, nome FROM material", conexao);
            await using MySqlDataReader leitor = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await leitor.ReadAsync().ConfigureAwait(false))
                nomesMateriais[leitor.GetInt32(0)] = leitor.GetString(1);
        }

        // SELECT de todas as simulacoes que tem pelo menos uma iteracao gravada (mais recente primeiro).
        // O MAX(iteracao) usa o indice (id_simulacao, iteracao), entao e rapido mesmo com milhoes de linhas
        public async Task<List<ResumoSimulacao>> ListarSimulacoesAsync()
        {
            var lista = new List<ResumoSimulacao>();
            using var cmd = new MySqlCommand(
                "SELECT s.id_simulacao, s.tamanho_n, s.temperaturas_aleatorias, s.processamento_paralelo, s.quantidade_blocos, " +
                "s.status, s.data_inicio, s.tempo_equilibrio_s, " +
                "(SELECT MAX(c.iteracao) FROM corpo c WHERE c.id_simulacao = s.id_simulacao) AS ultima_iteracao " +
                "FROM simulacao s ORDER BY s.id_simulacao DESC", conexao);
            await using MySqlDataReader leitor = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await leitor.ReadAsync().ConfigureAwait(false))
            {
                if (leitor.IsDBNull(8)) continue; // simulacao sem nenhum corpo gravado (ex.: o banco caiu logo no inicio): nao tem o que reprisar

                lista.Add(new ResumoSimulacao(
                    leitor.GetInt32(0),
                    leitor.GetInt32(1),
                    leitor.GetBoolean(2),
                    leitor.GetBoolean(3),
                    leitor.GetInt32(4),
                    leitor.GetString(5),
                    leitor.GetDateTime(6),
                    leitor.IsDBNull(7) ? null : leitor.GetDouble(7), // NULL quando nao chegou ao equilibrio
                    leitor.GetInt32(8)));
            }
            return lista;
        }

        // SELECT dos N² corpos de UMA iteracao. Devolve no mesmo formato que o Simulador.CriarRegistro gera,
        // entao a "fotografia" que foi gravada volta exatamente igual
        public async Task<IteracaoRegistro> CarregarIteracaoAsync(int idSimulacao, int iteracao, int n)
        {
            var estados = new EstadoCorpoRegistro[n * n];
            int quantidade = 0;
            double tempoSimulado = 0;

            using var cmd = new MySqlCommand(
                "SELECT linha, coluna, id_material, lado_m, temperatura_k, massa_kg, calor_sensivel_j, tempo_simulado_s " +
                "FROM corpo WHERE id_simulacao = @sim AND iteracao = @it", conexao); // usa o indice (id_simulacao, iteracao)
            cmd.Parameters.AddWithValue("@sim", idSimulacao);
            cmd.Parameters.AddWithValue("@it", iteracao);

            await using MySqlDataReader leitor = await cmd.ExecuteReaderAsync().ConfigureAwait(false);
            while (await leitor.ReadAsync().ConfigureAwait(false))
            {
                if (quantidade == estados.Length) // mais linhas que cubos: o mesmo cubo foi gravado duas vezes
                    throw new InvalidOperationException("A iteracao " + iteracao + " da simulacao " + idSimulacao + " tem corpos repetidos.");

                estados[quantidade++] = new EstadoCorpoRegistro(
                    leitor.GetInt32(0),                        // linha
                    leitor.GetInt32(1),                        // coluna
                    nomesMateriais[leitor.GetInt32(2)],        // id_material -> nome
                    leitor.GetDouble(3),                       // lado
                    leitor.GetDouble(4),                       // temperatura
                    leitor.GetDouble(5),                       // massa
                    leitor.GetDouble(6));                      // calor sensivel
                tempoSimulado = leitor.GetDouble(7); // igual em todas as linhas da iteracao
            }

            if (quantidade != estados.Length) // menos linhas que cubos: iteracao incompleta (nao deveria acontecer por causa da transacao)
                throw new InvalidOperationException("A iteracao " + iteracao + " da simulacao " + idSimulacao + " tem " + quantidade + " corpos, esperado " + estados.Length + ".");

            return new IteracaoRegistro(iteracao, tempoSimulado, estados);
        }

        public ValueTask DisposeAsync() // fecha a conexao quando a reprise termina
        {
            return conexao.DisposeAsync();
        }
    }
}

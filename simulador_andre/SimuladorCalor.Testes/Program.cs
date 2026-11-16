// DUPLA --->   João Vitor Moraes  & André Dalvi
//
// Testes de simulacao: roda a mesma logica da tela (Simulador + GravadorBanco) sem abrir a janela.
//
//   dotnet run --project SimuladorCalor.Testes -c Release
//
// A string de conexao vem de ConfiguracaoBanco.cs. Para usar outra (ex.: outra porta ou senha),
// defina a variavel de ambiente SIMULADOR_CONEXAO antes de rodar.
// Se o MySQL nao estiver ligado, os testes de banco sao pulados e os de simulacao rodam normalmente.

using System.Diagnostics;
using MySqlConnector;

namespace SimuladorCalor.Testes
{
    internal static class Program
    {
        private const double Dt = 1.0; // mesmo dt da tela (Form1.Dt)
        private static int falhas; // quantos testes falharam (vira o codigo de saida do programa)

        private static async Task<int> Main()
        {
            Console.WriteLine("Nucleos logicos deste PC: " + Environment.ProcessorCount);
            Console.WriteLine();

            TestarGradeDeBlocos();
            TestarParaleloIgualSequencial();
            TestarContraAlgoritmoOriginal();
            TestarConservacaoDeEnergia();
            TestarEquilibrio();
            TestarDesempenho();

            string conexao = Environment.GetEnvironmentVariable("SIMULADOR_CONEXAO") ?? ConfiguracaoBanco.StringConexao;
            string? erroConexao = GravadorBanco.TestarConexao(conexao);
            if (erroConexao == null)
            {
                await TestarBancoFluxoCompleto(conexao);
                await TestarBancoMatrizGrande(conexao);
            }
            else
            {
                Console.WriteLine("[PULADO] testes de banco: nao conectou ao MySQL (" + erroConexao + ")");
            }
            TestarErroDeConexao();

            Console.WriteLine();
            Console.WriteLine(falhas == 0 ? "TODOS OS TESTES PASSARAM" : falhas + " TESTE(S) FALHARAM");
            return falhas;
        }

        // ===================== AJUDANTES =====================

        private static void Verificar(bool condicao, string descricao) // imprime OK ou FALHOU e conta as falhas
        {
            Console.WriteLine((condicao ? "[OK]     " : "[FALHOU] ") + descricao);
            if (!condicao) falhas++;
        }

        // Cria uma matriz com materiais e temperaturas sorteados. A mesma semente gera sempre a mesma matriz,
        // assim duas matrizes criadas com a mesma semente comecam exatamente iguais
        private static Simulador CriarMatriz(int n, int nucleos, int semente, Material[]? materiais = null)
        {
            materiais ??= Material.Todos.ToArray(); // se nao informar, sorteia entre todos os materiais
            Random aleatorio = new Random(semente);
            Simulador sim = new Simulador(n, materiais[0], 0.1, 300, nucleos);
            foreach (Corpo c in sim.Corpos)
            {
                c.Material = materiais[aleatorio.Next(materiais.Length)];
                c.Temperatura = Math.Max(1, 300 + aleatorio.Next(-100, 101)); // igual ao "Temperaturas Aleatorias" da tela
            }
            sim.Corpos[n / 2, n / 2].Temperatura = 2000; // um ponto bem quente no meio, para ter bastante troca de calor
            return sim;
        }

        // maior diferenca de temperatura entre os cubos de duas matrizes (0 = iguais bit a bit)
        private static double MaiorDiferenca(Corpo[,] a, Corpo[,] b)
        {
            double maior = 0;
            int n = a.GetLength(0);
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    maior = Math.Max(maior, Math.Abs(a[i, j].Temperatura - b[i, j].Temperatura));
            return maior;
        }

        // O algoritmo ORIGINAL da dupla (antes da divisao em blocos): percorre a matriz inteira de uma vez,
        // sem threads. Serve de referencia para provar que a versao em blocos calcula a mesma fisica
        private static void PassoOriginal(Corpo[,] corpos, double dt)
        {
            int n = corpos.GetLength(0);
            double[,] energia = new double[n, n];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    if (j + 1 < n) // vizinho da direita
                    {
                        double e = Simulador.CalcularFluxo(corpos[i, j], corpos[i, j + 1]) * dt;
                        energia[i, j] -= e;
                        energia[i, j + 1] += e;
                    }
                    if (i + 1 < n) // vizinho de baixo
                    {
                        double e = Simulador.CalcularFluxo(corpos[i, j], corpos[i + 1, j]) * dt;
                        energia[i, j] -= e;
                        energia[i + 1, j] += e;
                    }
                }
            }
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    corpos[i, j].ReceberCalor(energia[i, j]);
        }

        // ===================== TESTES DA SIMULACAO =====================

        private static void TestarGradeDeBlocos()
        {
            Console.WriteLine("--- Grade de blocos ---");
            // (nucleos, n) -> (vertical, horizontal) esperado
            var casos = new (int nucleos, int n, int v, int h)[]
            {
                (1, 200, 1, 1), (2, 200, 1, 2), (4, 200, 2, 2), (6, 200, 2, 3), (8, 200, 2, 4),
                (12, 200, 3, 4), (16, 200, 4, 4), (7, 200, 1, 7), // 7 e primo: so da 1x7
                (8, 2, 2, 2), (16, 3, 3, 3)                       // matriz pequena: a grade e limitada a N
            };
            foreach (var c in casos)
            {
                (int v, int h) = Simulador.CalcularGradeDeBlocos(c.nucleos, c.n);
                Verificar(v == c.v && h == c.h, $"{c.nucleos,2} nucleos, matriz {c.n}x{c.n} -> grade {v}x{h} (esperado {c.v}x{c.h})");
            }
        }

        // O modo paralelo (Parallel.Invoke) e o sequencial (mesmos blocos, um depois do outro) tem que dar
        // EXATAMENTE o mesmo resultado: se uma thread escrevesse na area de outra, os numeros mudariam
        private static void TestarParaleloIgualSequencial()
        {
            Console.WriteLine("--- Paralelo x sequencial (mesmos blocos) ---");
            int[] nucleosTestados = { 2, 4, 6, 8, 12, 16, Environment.ProcessorCount };
            int[] tamanhos = { 2, 3, 5, 17, 50, 200 };
            foreach (int nucleos in nucleosTestados.Distinct())
            {
                double pior = 0;
                foreach (int n in tamanhos)
                {
                    Simulador paralelo = CriarMatriz(n, nucleos, semente: n);
                    Simulador sequencial = CriarMatriz(n, nucleos, semente: n);
                    paralelo.UsarParalelismo = true;
                    sequencial.UsarParalelismo = false;

                    int passos = n >= 200 ? 30 : 200;
                    for (int p = 0; p < passos; p++)
                    {
                        paralelo.Passo(Dt);
                        sequencial.Passo(Dt);
                    }
                    pior = Math.Max(pior, MaiorDiferenca(paralelo.Corpos, sequencial.Corpos));
                }
                Verificar(pior == 0, $"{nucleos,2} nucleos, matrizes 2x2 a 200x200: diferenca maxima = {pior} K (tem que ser 0)");
            }
        }

        // Compara com o algoritmo original. A diferenca nao e zero porque, nos pares de fronteira, a energia
        // e somada em outra ordem, e em double (a + b) + c pode diferir de a + (b + c) no ultimo digito
        private static void TestarContraAlgoritmoOriginal()
        {
            Console.WriteLine("--- Blocos x algoritmo original ---");
            foreach (int n in new[] { 2, 5, 17, 50, 200 })
            {
                Simulador blocos = CriarMatriz(n, Environment.ProcessorCount, semente: 100 + n);
                Simulador referencia = CriarMatriz(n, 1, semente: 100 + n); // so usado para copiar o estado inicial
                Corpo[,] original = referencia.Corpos;

                int passos = n >= 200 ? 30 : 300;
                for (int p = 0; p < passos; p++)
                {
                    blocos.Passo(Dt);
                    PassoOriginal(original, Dt);
                }
                double diferenca = MaiorDiferenca(blocos.Corpos, original);
                Verificar(diferenca < 1e-9, $"matriz {n}x{n}, {passos} passos: diferenca maxima = {diferenca:E2} K (so arredondamento)");
            }
        }

        // A energia que um cubo perde e exatamente a que o vizinho ganha: o calor total nao pode mudar
        private static void TestarConservacaoDeEnergia()
        {
            Console.WriteLine("--- Conservacao de energia ---");
            Simulador sim = CriarMatriz(50, Environment.ProcessorCount, semente: 7);
            double antes = sim.CalorTotal();
            for (int p = 0; p < 2000; p++) sim.Passo(Dt);
            double depois = sim.CalorTotal();
            double variacao = Math.Abs(depois - antes) / antes;
            Verificar(variacao < 1e-12, $"50x50 materiais misturados, 2000 passos: variacao relativa do calor total = {variacao:E2}");

            // a temperatura nunca pode sair do intervalo inicial (o calor so vai do quente para o frio)
            Simulador sim2 = CriarMatriz(30, Environment.ProcessorCount, semente: 8);
            double min0 = sim2.MenorTemperatura(), max0 = sim2.MaiorTemperatura();
            for (int p = 0; p < 2000; p++) sim2.Passo(Dt);
            Verificar(sim2.MenorTemperatura() >= min0 - 1e-9 && sim2.MaiorTemperatura() <= max0 + 1e-9,
                $"temperaturas ficaram entre {min0} K e {max0} K (simulacao estavel)");
        }

        // Mesmo criterio da tela: para quando maior - menor < 0,01 K
        private static void TestarEquilibrio()
        {
            Console.WriteLine("--- Equilibrio termico ---");
            Material cobre = Material.Todos.First(m => m.Nome == "Cobre");
            Simulador sim = CriarMatriz(5, Environment.ProcessorCount, semente: 3, new[] { cobre }); // valores padrao da tela: 5x5, cobre
            double media = 0;
            foreach (Corpo c in sim.Corpos) media += c.Temperatura;
            media /= 25; // com um so material e mesma massa, o equilibrio e a media das temperaturas

            int passos = 0;
            while (sim.MaiorTemperatura() - sim.MenorTemperatura() >= 0.01 && passos < 10_000_000)
            {
                sim.Passo(Dt);
                passos++;
            }
            bool chegou = sim.MaiorTemperatura() - sim.MenorTemperatura() < 0.01;
            Verificar(chegou, $"5x5 cobre chegou ao equilibrio em {sim.TempoSimulado:N0} s simulados");
            Verificar(Math.Abs(sim.MenorTemperatura() - media) < 0.01,
                $"temperatura final {sim.MenorTemperatura():F4} K = media inicial {media:F4} K");
        }

        private static void TestarDesempenho()
        {
            Console.WriteLine("--- Desempenho 200x200 (40.000 cubos) ---");
            Simulador sequencial = CriarMatriz(200, Environment.ProcessorCount, semente: 1);
            Simulador paralelo = CriarMatriz(200, Environment.ProcessorCount, semente: 1);
            sequencial.UsarParalelismo = false;
            paralelo.UsarParalelismo = true;
            for (int p = 0; p < 20; p++) { sequencial.Passo(Dt); paralelo.Passo(Dt); } // aquece (o JIT compila os metodos)

            const int passos = 300;
            Stopwatch cronometro = Stopwatch.StartNew();
            for (int p = 0; p < passos; p++) sequencial.Passo(Dt);
            double msSequencial = cronometro.Elapsed.TotalMilliseconds;

            cronometro.Restart();
            for (int p = 0; p < passos; p++) paralelo.Passo(Dt);
            double msParalelo = cronometro.Elapsed.TotalMilliseconds;

            Console.WriteLine($"         grade {paralelo.BlocosNaVertical}x{paralelo.BlocosNaHorizontal} = {paralelo.QuantidadeBlocos} blocos, {passos} passos");
            Console.WriteLine($"         sequencial: {msSequencial,8:F1} ms ({msSequencial / passos:F3} ms/passo)");
            Console.WriteLine($"         paralelo:   {msParalelo,8:F1} ms ({msParalelo / passos:F3} ms/passo)  -> {msSequencial / msParalelo:F2}x mais rapido");
            Verificar(MaiorDiferenca(sequencial.Corpos, paralelo.Corpos) == 0, "resultado identico nos dois modos");
            if (paralelo.QuantidadeBlocos > 1)
                Verificar(msParalelo < msSequencial, "paralelo mais rapido que sequencial na matriz grande");
        }

        // ===================== TESTES DO BANCO =====================

        // Faz exatamente o que a tela faz: Iniciar -> varios ticks -> edita um cubo no meio -> equilibrio.
        // Depois le tudo de volta do banco e confere se da para remontar a simulacao
        private static async Task TestarBancoFluxoCompleto(string conexao)
        {
            Console.WriteLine("--- Banco: simulacao completa ate o equilibrio (fluxo da tela) ---");
            Material[] rapidos = Material.Todos.Where(m => m.K > 200).ToArray(); // so materiais bons condutores, para equilibrar logo
            Simulador sim = CriarMatriz(10, Environment.ProcessorCount, semente: 42, rapidos);
            sim.UsarParalelismo = true;
            const int passosPorTick = 500;

            GravadorBanco gravador = new GravadorBanco(conexao);
            int numeroIteracao = 0;
            double tempoProcessamentoTotalMs = 0;
            List<IteracaoRegistro> enviadas = new List<IteracaoRegistro>(); // guarda as fotos enviadas, para conferir a reprise depois

            // "Iniciar": abre o registro com a iteracao 0 (btnIniciar_Click -> IniciarGravacao)
            enviadas.Add(sim.CriarRegistro(numeroIteracao++));
            gravador.Enviar(new CmdNovaSimulacao(sim.N, Dt, true, sim.UsarParalelismo, sim.QuantidadeBlocos,
                DateTime.Now, enviadas[^1]));

            int ticks = 0;
            while (sim.MaiorTemperatura() - sim.MenorTemperatura() >= 0.01 && ticks < 5000)
            {
                // fila cheia: a tela pula o tick ate o banco alcancar (timer1_Tick)
                while (gravador.Pendentes > ConfiguracaoBanco.LimiteIteracoesPendentes && gravador.Erro == null)
                    await Task.Delay(5);

                // os passos rodam em outra thread, igual ao timer1_Tick
                double duracaoMs = await Task.Run(() =>
                {
                    Stopwatch cronometro = Stopwatch.StartNew();
                    for (int p = 0; p < passosPorTick; p++) sim.Passo(Dt);
                    return cronometro.Elapsed.TotalMilliseconds;
                });
                tempoProcessamentoTotalMs += duracaoMs;
                enviadas.Add(sim.CriarRegistro(numeroIteracao++));
                gravador.Enviar(new CmdSalvarIteracao(enviadas[^1]));
                ticks++;

                if (ticks == 5) // "Aplicar no cubo" no meio da simulacao (btnAplicar_Click)
                {
                    Corpo c = sim.Corpos[3, 4];
                    c.Material = Material.Todos.First(m => m.Nome == "Ouro");
                    c.Temperatura = 900;
                    enviadas.Add(sim.CriarRegistro(numeroIteracao++));
                    gravador.Enviar(new CmdSalvarIteracao(enviadas[^1]));
                }
            }
            bool equilibrou = sim.MaiorTemperatura() - sim.MenorTemperatura() < 0.01;
            Verificar(equilibrou, $"equilibrio em {sim.TempoSimulado:N0} s simulados, {ticks} ticks, {numeroIteracao} iteracoes enviadas");

            // espera o id gerado pelo banco aparecer (o INSERT roda na outra thread)
            long idSimulacao = await EsperarId(gravador);

            // "Equilibrio": fecha o registro (FinalizarGravacao)
            gravador.Enviar(new CmdFinalizarSimulacao(StatusSimulacao.EQUILIBRIO, sim.TempoSimulado, tempoProcessamentoTotalMs, DateTime.Now));
            bool gravouTudo = gravador.Encerrar(TimeSpan.FromSeconds(60)); // igual ao Form1_FormClosing
            Verificar(gravouTudo, $"fila esvaziou sem erro (erro = {gravador.Erro ?? "nenhum"}), {gravador.IteracoesGravadas} iteracoes gravadas");

            // ---- confere o que ficou no banco ----
            await using var con = new MySqlConnection(conexao);
            await con.OpenAsync();

            using (var cmd = new MySqlCommand("SELECT status, tempo_equilibrio_s, data_fim, tempo_processamento_ms, quantidade_blocos, " +
                                              "processamento_paralelo FROM simulacao WHERE id_simulacao = @id", con))
            {
                cmd.Parameters.AddWithValue("@id", idSimulacao);
                await using var leitor = await cmd.ExecuteReaderAsync();
                Verificar(await leitor.ReadAsync(), $"simulacao #{idSimulacao} existe na tabela simulacao");
                Verificar(leitor.GetString(0) == "EQUILIBRIO", "status = " + leitor.GetString(0));
                Verificar(leitor.GetDouble(1) == sim.TempoSimulado, $"tempo_equilibrio_s = {leitor.GetDouble(1):N0}");
                Verificar(!leitor.IsDBNull(2), "data_fim preenchida pelo UPDATE");
                Verificar(Math.Abs(leitor.GetDouble(3) - tempoProcessamentoTotalMs) < 1e-6, $"tempo_processamento_ms = {leitor.GetDouble(3):F1}");
                Verificar(leitor.GetInt32(4) == sim.QuantidadeBlocos && leitor.GetBoolean(5), $"quantidade_blocos = {leitor.GetInt32(4)}, paralelo = {leitor.GetBoolean(5)}");
            }

            using (var cmd = new MySqlCommand("SELECT COUNT(*), COUNT(DISTINCT iteracao), MAX(iteracao) FROM corpo WHERE id_simulacao = @id", con))
            {
                cmd.Parameters.AddWithValue("@id", idSimulacao);
                await using var leitor = await cmd.ExecuteReaderAsync();
                await leitor.ReadAsync();
                long linhas = leitor.GetInt64(0);
                long iteracoes = leitor.GetInt64(1);
                int maior = leitor.GetInt32(2);
                Verificar(iteracoes == numeroIteracao && maior == numeroIteracao - 1, $"{iteracoes} iteracoes no banco (0 a {maior}), nenhuma perdida");
                Verificar(linhas == (long)numeroIteracao * sim.N * sim.N, $"{linhas} linhas na tabela corpo = iteracoes x N²");
            }

            // ---- remontagem: carrega a iteracao k do banco, roda os passos e compara com a k+1 do banco ----
            var iteracoesBanco = await CarregarSimulacao(con, idSimulacao, sim.N);
            int conferidas = 0, edicoes = 0;
            double piorRemontagem = 0;
            for (int k = 0; k + 1 < iteracoesBanco.Count; k++)
            {
                (double tempoK, Corpo[,] estadoK) = iteracoesBanco[k];
                (double tempoProx, Corpo[,] estadoProx) = iteracoesBanco[k + 1];
                int passos = (int)Math.Round((tempoProx - tempoK) / Dt); // passos entre as duas iteracoes
                if (passos == 0) { edicoes++; continue; } // tempo igual = edicao do usuario, nao tem o que recalcular

                Simulador remontado = new Simulador(sim.N, Material.Todos[0], 0.1, 300);
                for (int i = 0; i < sim.N; i++)
                    for (int j = 0; j < sim.N; j++)
                    {
                        remontado.Corpos[i, j].Material = estadoK[i, j].Material;
                        remontado.Corpos[i, j].Lado = estadoK[i, j].Lado;
                        remontado.Corpos[i, j].Temperatura = estadoK[i, j].Temperatura;
                    }
                for (int p = 0; p < passos; p++) remontado.Passo(Dt);
                piorRemontagem = Math.Max(piorRemontagem, MaiorDiferenca(remontado.Corpos, estadoProx));
                conferidas++;
            }
            Verificar(piorRemontagem == 0 && edicoes == 1,
                $"remontagem a partir do banco: {conferidas} iteracoes reproduzidas bit a bit, {edicoes} edicao detectada");

            // o estado final no banco e o mesmo da memoria
            Verificar(MaiorDiferenca(iteracoesBanco[^1].corpos, sim.Corpos) == 0, "ultima iteracao do banco = estado final da simulacao");

            await TestarReprise(conexao, idSimulacao, enviadas, sim.TempoSimulado);
        }

        // REPRISE (botao "Reprisar do banco"): faz o mesmo que o Form1.ReproduzirAsync, sem a janela.
        // Le cada iteracao do banco, copia para a matriz (CarregarEstado) e confere se a "foto" que sai da matriz
        // e identica a que foi tirada durante a simulacao. Nenhum Passo() e chamado aqui
        private static async Task TestarReprise(string conexao, long idSimulacao, List<IteracaoRegistro> enviadas, double tempoEquilibrio)
        {
            Console.WriteLine("--- Reprise: rever a simulacao gravada sem recalcular ---");
            await using LeitorBanco leitor = await LeitorBanco.AbrirAsync(conexao);

            List<ResumoSimulacao> lista = await leitor.ListarSimulacoesAsync();
            ResumoSimulacao? resumo = lista.Find(s => s.Id == idSimulacao);
            Verificar(resumo != null, $"simulacao #{idSimulacao} aparece na lista da janela de escolha: {resumo}");
            if (resumo == null) return;
            Verificar(resumo.UltimaIteracao == enviadas.Count - 1 && resumo.Status == "EQUILIBRIO" && resumo.TempoEquilibrio == tempoEquilibrio,
                $"resumo correto: iteracoes 0 a {resumo.UltimaIteracao}, {resumo.Status}, equilibrio em {resumo.TempoEquilibrio:N0} s");

            Simulador tela = new Simulador(resumo.N, Material.Todos[0], 0.1, 300); // a matriz que a tela desenha durante a reprise
            int iguais = 0;
            Task<IteracaoRegistro> proxima = leitor.CarregarIteracaoAsync(resumo.Id, 0, resumo.N); // mesmo pre-carregamento da tela
            for (int k = 0; k <= resumo.UltimaIteracao; k++)
            {
                IteracaoRegistro atual = await proxima;
                if (k < resumo.UltimaIteracao) proxima = leitor.CarregarIteracaoAsync(resumo.Id, k + 1, resumo.N);

                tela.CarregarEstado(atual);
                IteracaoRegistro foto = tela.CriarRegistro(k); // a foto do que esta "na tela" agora
                // records comparam pelo valor: linha, coluna, material, lado, temperatura, massa e calor sensivel, campo a campo
                if (foto.TempoSimulado == enviadas[k].TempoSimulado && foto.Corpos.SequenceEqual(enviadas[k].Corpos))
                    iguais++;
            }
            Verificar(iguais == enviadas.Count, $"{iguais} de {enviadas.Count} quadros da reprise identicos aos da simulacao original (bit a bit, incluindo a edicao)");
            Verificar(tela.TempoSimulado == tempoEquilibrio, $"ultimo quadro mostra o tempo {tela.TempoSimulado:N0} s (o tempo veio do banco, nao foi calculado)");
        }

        // Matriz maxima da tela (200x200): mede quanto tempo o banco leva para gravar cada iteracao
        // e testa o fechamento como INTERROMPIDA (usuario criou outra matriz ou fechou o programa)
        private static async Task TestarBancoMatrizGrande(string conexao)
        {
            Console.WriteLine("--- Banco: matriz 200x200 interrompida ---");
            Simulador sim = CriarMatriz(200, Environment.ProcessorCount, semente: 9);
            GravadorBanco gravador = new GravadorBanco(conexao);
            int numeroIteracao = 0;

            Stopwatch cronometro = Stopwatch.StartNew();
            gravador.Enviar(new CmdNovaSimulacao(sim.N, Dt, true, true, sim.QuantidadeBlocos, DateTime.Now, sim.CriarRegistro(numeroIteracao++)));
            const int ticks = 5;
            IteracaoRegistro ultimaEnviada = sim.CriarRegistro(-1);
            for (int t = 0; t < ticks; t++)
            {
                await Task.Run(() => { for (int p = 0; p < 100; p++) sim.Passo(Dt); });
                ultimaEnviada = sim.CriarRegistro(numeroIteracao++);
                gravador.Enviar(new CmdSalvarIteracao(ultimaEnviada));
            }
            long idSimulacao = await EsperarId(gravador);
            gravador.Enviar(new CmdFinalizarSimulacao(StatusSimulacao.INTERROMPIDA, null, 0, DateTime.Now));
            bool gravouTudo = gravador.Encerrar(TimeSpan.FromSeconds(120));
            double segundos = cronometro.Elapsed.TotalSeconds;

            Verificar(gravouTudo, $"{numeroIteracao} iteracoes x 40.000 corpos gravadas em {segundos:F1} s ({segundos * 1000 / numeroIteracao:F0} ms por iteracao)");

            await using var con = new MySqlConnection(conexao);
            await con.OpenAsync();
            using var cmd = new MySqlCommand("SELECT s.status, s.tempo_equilibrio_s, (SELECT COUNT(*) FROM corpo c WHERE c.id_simulacao = s.id_simulacao) " +
                                             "FROM simulacao s WHERE s.id_simulacao = @id", con);
            cmd.Parameters.AddWithValue("@id", idSimulacao);
            await using var leitor = await cmd.ExecuteReaderAsync();
            await leitor.ReadAsync();
            Verificar(leitor.GetString(0) == "INTERROMPIDA" && leitor.IsDBNull(1), "status = INTERROMPIDA e tempo_equilibrio_s = NULL");
            Verificar(leitor.GetInt64(2) == numeroIteracao * 40_000L, $"{leitor.GetInt64(2)} linhas de corpo gravadas");

            // reprise da matriz grande: quanto tempo leva para ler cada quadro de 40.000 cubos
            await using LeitorBanco leitorReprise = await LeitorBanco.AbrirAsync(conexao);
            Simulador tela = new Simulador(200, Material.Todos[0], 0.1, 300);
            cronometro.Restart();
            for (int k = 0; k < numeroIteracao; k++)
                tela.CarregarEstado(await leitorReprise.CarregarIteracaoAsync((int)idSimulacao, k, 200));
            double msPorQuadro = cronometro.Elapsed.TotalMilliseconds / numeroIteracao;
            IteracaoRegistro fotoFinal = tela.CriarRegistro(ultimaEnviada.Numero);
            Verificar(fotoFinal.Corpos.SequenceEqual(ultimaEnviada.Corpos) && fotoFinal.TempoSimulado == ultimaEnviada.TempoSimulado,
                $"reprise 200x200: {msPorQuadro:F0} ms para ler cada quadro, ultimo quadro identico ao calculado");
        }

        // Servidor desligado ou porta errada: a tela tem que receber a mensagem de erro, e nao travar
        private static void TestarErroDeConexao()
        {
            Console.WriteLine("--- Banco: servidor inexistente ---");
            Stopwatch cronometro = Stopwatch.StartNew();
            string? erro = GravadorBanco.TestarConexao("Server=127.0.0.1;Port=1;Database=simulador_calor;User ID=root;Password=;Connection Timeout=3;");
            Verificar(erro != null && cronometro.Elapsed.TotalSeconds < 10, $"TestarConexao devolveu o erro em {cronometro.Elapsed.TotalSeconds:F1} s: {erro}");
        }

        private static async Task<long> EsperarId(GravadorBanco gravador)
        {
            Stopwatch cronometro = Stopwatch.StartNew();
            while (gravador.IdSimulacaoAtual <= 0 && gravador.Erro == null && cronometro.Elapsed.TotalSeconds < 30)
                await Task.Delay(5);
            if (gravador.Erro != null) throw new Exception("Erro no gravador: " + gravador.Erro);
            return gravador.IdSimulacaoAtual;
        }

        // SELECT de remontagem (o mesmo da DOCUMENTACAO.md): devolve, para cada iteracao, o tempo e a matriz de cubos
        private static async Task<List<(double tempo, Corpo[,] corpos)>> CarregarSimulacao(MySqlConnection con, long idSimulacao, int n)
        {
            var resultado = new List<(double tempo, Corpo[,] corpos)>();
            using var cmd = new MySqlCommand(
                "SELECT c.iteracao, c.tempo_simulado_s, c.linha, c.coluna, m.nome, c.lado_m, c.temperatura_k " +
                "FROM corpo c JOIN material m ON m.id_material = c.id_material " +
                "WHERE c.id_simulacao = @id ORDER BY c.iteracao, c.linha, c.coluna", con);
            cmd.Parameters.AddWithValue("@id", idSimulacao);
            await using var leitor = await cmd.ExecuteReaderAsync();
            while (await leitor.ReadAsync())
            {
                int iteracao = leitor.GetInt32(0);
                if (iteracao == resultado.Count) // primeira linha de uma nova iteracao
                    resultado.Add((leitor.GetDouble(1), new Corpo[n, n]));
                Material material = Material.Todos.First(m => m.Nome == leitor.GetString(4));
                resultado[iteracao].corpos[leitor.GetInt32(2), leitor.GetInt32(3)] = new Corpo(material, leitor.GetDouble(5), leitor.GetDouble(6));
            }
            return resultado;
        }
    }
}

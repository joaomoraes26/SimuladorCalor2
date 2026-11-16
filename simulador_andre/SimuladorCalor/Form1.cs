// DUPLA --->   João Vitor Moraes  & André Dalvi

using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace SimuladorCalor
{
    public partial class Form1 : Form
    {
        private Simulador simulador = null!; // fica null ate o usuario clicar em "Criar Matriz" (os metodos checam antes de usar)
        private int linhaSelecionada = -1;
        private int colunaSelecionada = -1;
        private double escalaMin;
        private double escalaMax;
        private Random aleatorio = new Random();

        // ===== Desenho =====
        private const float TamanhoMinimoDetalhado = 6f; // abaixo disso (pixels por cubo) desenha como imagem, um pixel por cubo
        private const float TamanhoMinimoTexto = 35f;    // abaixo disso o texto nao cabe dentro do cubo
        private Bitmap? imagemGrade;                      // reaproveitada entre os desenhos das matrizes grandes

        // ===== Calculo em segundo plano =====
        private bool calculando; // true enquanto os passos rodam em outra thread
        private bool fechando;   // true quando a janela esta sendo fechada

        // ===== Gravacao no banco =====
        private const double Dt = 1.0; // cada passo simula 1 segundo
        private GravadorBanco? gravador; // criado na primeira vez que a gravacao e usada
        private bool gravandoSimulacao; // true enquanto a simulacao atual tem um registro aberto no banco
        private int numeroIteracao; // numero da proxima iteracao a ser gravada (0 = estado inicial)
        private bool temperaturasAleatorias; // guarda como a matriz atual foi criada
        private double ultimaDuracaoMs; // tempo de processamento do ultimo tick
        private double tempoProcessamentoTotalMs; // soma dos ticks da simulacao gravada (vai para o banco no final)

        // ===== Reprise (mostra de novo uma simulacao gravada, sem calcular) =====
        private CancellationTokenSource? cancelarReprise; // diferente de null enquanto uma reprise esta passando; Cancel() para ela
        private ResumoSimulacao? resumoReprise;           // qual simulacao esta sendo reprisada
        private int iteracaoReprise;                      // qual iteracao esta na tela agora
        private bool Reprisando => cancelarReprise != null;

        public Form1()
        {
            InitializeComponent();
        }

        private void btnCriar_Click(object sender, EventArgs e)
        {
            PararReprise(); // se uma reprise estiver passando, para ela (a matriz nova substitui a da reprise)
            timer1.Stop(); // se a simulacao estiver rodando e o usuario quiser uma nova matriz, o timer para
            btnIniciar.Text = "Iniciar";
            FinalizarGravacao(StatusSimulacao.INTERROMPIDA); // se a matriz anterior estava sendo gravada, fecha o registro dela

            int n = (int)numN.Value; // o valor  do NumericUpDown é decimal, o (int) converte pra inteiro, do jeito que o simuladorp precisa 
            double lado = (double)numLado.Value; // mesma coisa, mas agora convertendo para double
            Material material = (Material)cmbMaterial.SelectedItem!; // o ! diz ao compilador que nunca e null (o Form1_Load ja seleciona o primeiro). a box guarda os itens como generico. o (Material) serve para puxar um material colocado na box 
            double temperatura = (double)numTemp.Value;

            simulador = new Simulador(n, material, lado, temperatura); // cria uma nova matriz com os cubos iguais 
            simulador.UsarParalelismo = chkParalelo.Checked;
            temperaturasAleatorias = chkAleatorio.Checked;

            if (chkAleatorio.Checked) // checa se o usuario pediu temperaturas aleatorias 
            {
                foreach (Corpo c in simulador.Corpos) // percorre todos os cubos e soma um numero entre -100 e 100 na temperatura 
                {
                    c.Temperatura = Math.Max(1, temperatura + aleatorio.Next(-100, 101)); // o Math.max garante que nao tenha nenhum numero abaixo de 1K, porque em kelvin a temperatura nao e negativa nem 0 
                }
            }

            linhaSelecionada = -1; // limpa a linha e mostra que nenhum cubo foi selecionado 
            colunaSelecionada = -1; // mesma coisa para a coluna 
            AtualizarInfo();
            escalaMin = simulador.MenorTemperatura(); // guarda a menor temperatura da matriz
            escalaMax = simulador.MaiorTemperatura(); // guarda a maior temperatura da matriz 
            picGrade.Invalidate(); // avisa o picture box para redesenhar uma nova matriz 
            AtualizarStatus();
            AtualizarStatusBanco();
        }

        private void btnAplicar_Click(object sender, EventArgs e)
        {
            if (simulador == null || linhaSelecionada < 0) // se nenhum cubo foi clicado, avisa ao usuario e sai 
            {
                MessageBox.Show("Clique em um cubo primeiro.");
                return;
            }
            if (calculando) return; // seguranca extra: o botao ja fica desabilitado durante o calculo
            if (Reprisando) // a reprise mostra o que foi gravado: editar no meio dela nao faria sentido
            {
                MessageBox.Show("Pare a reprise antes de editar um cubo.");
                return;
            }

            Corpo c = simulador.Corpos[linhaSelecionada, colunaSelecionada]; // pega o cubo selecionado
            c.Material = (Material)cmbMaterialCorpo.SelectedItem!; // troca o material do cubo
            c.Temperatura = (double)numTempCorpo.Value; // troca a temperatura, convertendo para double 

            // se a simulacao ja esta sendo gravada, grava a matriz logo apos a edicao como uma nova iteracao
            // (com o mesmo tempo simulado). Sem isso, a troca ficaria "escondida" entre dois ticks e
            // nao daria para remontar a simulacao exatamente
            if (gravandoSimulacao && gravador != null)
                gravador.Enviar(new CmdSalvarIteracao(simulador.CriarRegistro(numeroIteracao++)));

            escalaMin = simulador.MenorTemperatura(); // recalcula a escala das cores 
            escalaMax = simulador.MaiorTemperatura();
            AtualizarInfo(); // atualiza 
            picGrade.Invalidate(); // redesenha 
            AtualizarStatus();
            AtualizarStatusBanco();
        }

        private void btnIniciar_Click(object sender, EventArgs e)
        {
            if (simulador == null) // se a matriz nao foi criada, sai 
            {
                MessageBox.Show("Crie a matriz primeiro.");
                return;
            }
            if (Reprisando) // durante a reprise quem muda a tela e o banco, nao o calculo
            {
                MessageBox.Show("Pare a reprise antes de iniciar a simulação.\nQuando a reprise termina, dá para continuar simulando a partir da última iteração.");
                return;
            }

            if (timer1.Enabled) // verifica se a simulacao ja esta rodfando
            {
                timer1.Stop(); // se estava rodando, pausa, e o botao diz Iniciar 
                btnIniciar.Text = "Iniciar";
            }
            else
            {
                // se o usuario pausou no meio de um calculo, a outra thread ainda pode estar mudando as temperaturas.
                // Tirar a "foto" da iteracao 0 agora pegaria metade dos cubos antigos e metade novos (condicao de corrida),
                // entao espera o calculo terminar: e questao de milissegundos, basta clicar de novo
                if (calculando) return;

                // na primeira vez que inicia esta matriz, abre o registro no banco com o estado inicial (iteracao 0).
                // E aqui, e nao no "Criar Matriz", para que as edicoes feitas antes de iniciar facam parte do estado inicial
                if (!gravandoSimulacao && chkGravarBanco.Checked)
                    IniciarGravacao();

                timer1.Start();
                btnIniciar.Text = "Pausar"; // se estiver parado, aciona o botao, e ele diz Pausar  
            }
        }

        private void Form1_Load(object sender, EventArgs e) // roda uma vez, quando a janela abre
        {
            foreach (Material m in Material.Todos) // coloca todos os materiais nas duas caixas (a box mostra o ToString, que e o nome)
            {
                cmbMaterial.Items.Add(m);
                cmbMaterialCorpo.Items.Add(m);
            }
            cmbMaterial.SelectedIndex = 0; // ja deixa o primeiro material (Cobre) selecionado, assim SelectedItem nunca e null
            cmbMaterialCorpo.SelectedIndex = 0;
        }

        private void picGrade_Paint(object sender, PaintEventArgs e)
        {
            if (simulador == null) return; // se a matriz ainda nao foi criada, ele sai do metodo 

            float tamanho = Math.Min(picGrade.Width, picGrade.Height) / (float)simulador.N; // o tamanho de cada quadrado em pixel. pega o menor lado da pagina e divide por N

            if (tamanho >= TamanhoMinimoDetalhado)
                DesenharDetalhado(e.Graphics, tamanho);
            else
                DesenharComoImagem(e.Graphics, tamanho);

            if (linhaSelecionada >= 0)
            {
                float espessura = tamanho >= 10 ? 4 : 2; // em cubos pequenos uma borda grossa cobriria os vizinhos
                using (Pen caneta = new Pen(Color.Yellow, espessura))
                    e.Graphics.DrawRectangle(caneta, colunaSelecionada * tamanho, linhaSelecionada * tamanho, tamanho, tamanho); // se tiver um cubo selecionado, desenha uma borda em volta para dar contraste e mopstrar que foi selecionadop 
            }
        }

        // Matrizes pequenas: um retangulo por cubo, com borda e (se couber) o texto - igual a versao original
        private void DesenharDetalhado(Graphics g, float tamanho)
        {
            for (int i = 0; i < simulador.N; i++) // percorrem a matriz
            {
                for (int j = 0; j < simulador.N; j++)
                {
                    Corpo c = simulador.Corpos[i, j];
                    float x = j * tamanho; //posicao do quadrado na tela ---- x = horizontal,  y = vertical 
                    float y = i * tamanho;

                    using (SolidBrush pincel = new SolidBrush(CorDaTemperatura(c.Temperatura))) // pinta o quadrado de acordo com a cor da temperatura -- using solid brush libera o pincel da memoria depois de usado 
                        g.FillRectangle(pincel, x, y, tamanho, tamanho);
                    g.DrawRectangle(Pens.Black, x, y, tamanho, tamanho); // desenha a borda do retangulo

                    if (tamanho >= TamanhoMinimoTexto)
                    {
                        string texto = c.Material.Nome + "\n" + c.Temperatura.ToString("F1") + " K";
                        g.DrawString(texto, Font, Brushes.White, x + 3, y + 3); // escreve o nome do material e a temperatura -- "F1" = 1 casa decimal
                    }
                }
            }
        }

        // Matrizes grandes (ex.: 200x200 = 40.000 cubos): desenhar 40.000 retangulos seria lento.
        // Em vez disso, monta uma imagem pequena com UM PIXEL POR CUBO e amplia para o tamanho da tela.
        private void DesenharComoImagem(Graphics g, float tamanho)
        {
            int n = simulador.N;
            if (imagemGrade == null || imagemGrade.Width != n) // cria a imagem so quando o tamanho da matriz muda
            {
                imagemGrade?.Dispose();
                imagemGrade = new Bitmap(n, n);
            }

            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                    imagemGrade.SetPixel(j, i, CorDaTemperatura(simulador.Corpos[i, j].Temperatura)); // x = coluna, y = linha

            g.InterpolationMode = InterpolationMode.NearestNeighbor; // amplia sem borrar (cada cubo vira um quadrado nitido)
            g.PixelOffsetMode = PixelOffsetMode.Half; // sem isso a primeira linha e coluna aparecem cortadas pela metade
            g.DrawImage(imagemGrade, 0, 0, n * tamanho, n * tamanho);
        }

        private Color CorDaTemperatura(double temperatura)
        {
            double t = 0.5; // transforma a temperatura em um numero de 0 a 1 , sendo 0 a mais fria e 1 a mais quente 
            if (escalaMax > escalaMin) // se tiver temperaturas iguais, ocorreria um erro, assim ele mantem no 0.5 
                t = (temperatura - escalaMin) / (escalaMax - escalaMin);
            t = Math.Max(0, Math.Min(1, t)); // garante que o t fica entre o 0 e 1

            int vermelho = (int)(255 * t);
            int azul = (int)(255 * (1 - t));
            return Color.FromArgb(vermelho, 0, azul); // retorna a cor das temperaturas 
        }

        private void picGrade_MouseClick(object sender, MouseEventArgs e)
        {
            if (simulador == null) return; // se a matriz nao foi criada, sai do metodo 

            float tamanho = Math.Min(picGrade.Width, picGrade.Height) / (float)simulador.N; // tamanho de cada quadrado em pixel
            int coluna = (int)(e.X / tamanho); // descobre a coluna em que foi clicada dividindo o X do mouse pelo tamanho do quadrado
            int linha = (int)(e.Y / tamanho);
            if (linha >= simulador.N || coluna >= simulador.N) return; // se o clique foi fora da grade, ignora 

            linhaSelecionada = linha; // guarda qual cubo foi selecionado 
            colunaSelecionada = coluna;

            Corpo c = simulador.Corpos[linha, coluna]; // pega o cubo clicado na matriz 
            cmbMaterialCorpo.SelectedItem = c.Material; // mostra na caixa o material atual do cubo
            // mostra a temperatura atual. O Clamp segura o valor dentro do minimo/maximo da caixa (1 a 50000 K),
            // senao o NumericUpDown da erro ao receber um valor fora do intervalo
            numTempCorpo.Value = Math.Clamp((decimal)c.Temperatura, numTempCorpo.Minimum, numTempCorpo.Maximum);

            AtualizarInfo(); // atualiza o texto com dados do cubo
            picGrade.Invalidate(); // redesenha a grade pra aparecer a borda amarela 
        }

        private void AtualizarInfo() // mostra os dados do cubo
        {
            if (simulador == null || linhaSelecionada < 0) // se nao tem cubo selecionado, devolve ao usuario que nao tem nenhum cubo selecionado
            {
                lblInfo.Text = "Nenhum cubo selecionado";
                return;
            }

            Corpo c = simulador.Corpos[linhaSelecionada, colunaSelecionada]; // pega o cubo selecionado
            lblInfo.Text = "Posição: linha " + (linhaSelecionada + 1) + ", coluna " + (colunaSelecionada + 1) + "\n" // monta o texto com a posicao, o material, a temperatura, a massa e o calor sensivel 
                         + "Material: " + c.Material.Nome + "\n"
                         + "Temperatura: " + c.Temperatura.ToString("F2") + " K\n"
                         + "Massa: " + c.Massa.ToString("F3") + " kg\n"
                         + "Calor sensível: " + c.CalorSensivel.ToString("N0") + " J";
        }

        // Executado a cada 50 ms enquanto o timer esta ligado.
        // 'async': os passos rodam em OUTRA thread (Task.Run) e a tela continua respondendo.
        // Enquanto o 'await' espera, a janela pode ser movida, pausada etc.
        private async void timer1_Tick(object sender, EventArgs e)
        {
            if (calculando) return; // o tick anterior ainda esta calculando: pula este

            if (gravandoSimulacao && gravador != null)
            {
                if (gravador.Erro != null) // o banco falhou: avisa e continua sem gravar
                {
                    TratarErroBanco();
                }
                else if (gravador.Pendentes > ConfiguracaoBanco.LimiteIteracoesPendentes)
                {
                    // o banco esta mais lento que a simulacao: pula este tick e espera a fila diminuir.
                    // Assim nenhuma iteracao e perdida e a memoria nao cresce sem limite
                    AtualizarStatusBanco();
                    return;
                }
            }

            Simulador sim = simulador; // guarda QUAL matriz esta sendo calculada (o usuario pode criar outra durante o calculo)
            int passos = (int)numPassos.Value; // le a velocidade escolhida
            sim.UsarParalelismo = chkParalelo.Checked; // durante uma gravacao a caixa fica travada, entao o modo nao muda

            calculando = true;
            btnAplicar.Enabled = false; // impede editar um cubo enquanto outra thread altera as temperaturas (seria condicao de corrida)
            double duracaoMs;
            try
            {
                duracaoMs = await Task.Run(() =>
                {
                    Stopwatch cronometro = Stopwatch.StartNew(); // mede so o processamento, para comparar sequencial x paralelo
                    for (int p = 0; p < passos; p++) // roda varios passos de 1 segundo para poder atualizar - os passos pequenos facilitam os calculos 
                        sim.Passo(Dt);
                    return cronometro.Elapsed.TotalMilliseconds;
                });
            }
            finally
            {
                calculando = false;
                if (!fechando) btnAplicar.Enabled = true;
            }

            // enquanto calculava, a janela pode ter sido fechada ou outra matriz criada: descarta este resultado
            if (fechando || sim != simulador) return;

            ultimaDuracaoMs = duracaoMs;

            // cada atualizacao da tela = uma iteracao gravada no banco
            if (gravandoSimulacao && gravador != null)
            {
                tempoProcessamentoTotalMs += duracaoMs;
                gravador.Enviar(new CmdSalvarIteracao(sim.CriarRegistro(numeroIteracao++)));
            }

            // atualiza os textos, desenhos e cores 
            AtualizarInfo();
            AtualizarStatus();
            AtualizarStatusBanco();
            picGrade.Invalidate();

            if (sim.MaiorTemperatura() - sim.MenorTemperatura() < 0.01) // se a diferenca entre a maior e a menor temperatura for menor que 0,01 K, o equilibrio termico foi atingido
            {
                timer1.Stop();
                btnIniciar.Text = "Iniciar";
                FinalizarGravacao(StatusSimulacao.EQUILIBRIO); // fecha o registro no banco com o tempo de equilibrio
                AtualizarStatusBanco();
                MessageBox.Show("Equilíbrio térmico atingido em " + sim.TempoSimulado.ToString("N0") + " s simulados."); // para a simulacao e mostra quanto tempo demorou
            }
        }

        private void AtualizarStatus() // mostra o resumo da simulacao 
        {
            lblStatus.Text = "Tempo simulado: " + simulador.TempoSimulado.ToString("N0") + " s\n"
                           + "Menor temperatura: " + simulador.MenorTemperatura().ToString("F2") + " K\n"
                           + "Maior temperatura: " + simulador.MaiorTemperatura().ToString("F2") + " K\n"
                           + "Calor total: " + simulador.CalorTotal().ToString("N0") + " J";
        }

        // ===================== BANCO DE DADOS =====================

        // Abre o registro da simulacao no banco: INSERT na tabela simulacao + os N² corpos da iteracao 0
        private void IniciarGravacao()
        {
            if (gravador == null) // primeira gravacao desde que o programa abriu (ou depois de um erro)
            {
                string? erro = GravadorBanco.TestarConexao(ConfiguracaoBanco.StringConexao); // tenta conectar antes, para avisar o usuario na hora
                if (erro != null) // nao conectou: desliga a gravacao e a simulacao roda normalmente sem banco
                {
                    chkGravarBanco.Checked = false;
                    MessageBox.Show("Não foi possível conectar ao MySQL:\n" + erro +
                                    "\n\nA simulação vai rodar sem gravar. Confira a string em ConfiguracaoBanco.cs.");
                    AtualizarStatusBanco();
                    return;
                }
                gravador = new GravadorBanco(ConfiguracaoBanco.StringConexao); // cria o gravador, que ja inicia a thread do banco
            }

            simulador.UsarParalelismo = chkParalelo.Checked; // o modo gravado no banco e o modo que vai ser usado
            numeroIteracao = 0; // cada simulacao comeca a contar as iteracoes do zero
            tempoProcessamentoTotalMs = 0;

            // so coloca o comando na fila e segue: quem faz o INSERT e a thread do banco
            gravador.Enviar(new CmdNovaSimulacao(
                simulador.N, Dt, temperaturasAleatorias,
                simulador.UsarParalelismo, simulador.QuantidadeBlocos,
                DateTime.Now, simulador.CriarRegistro(numeroIteracao++))); // iteracao 0 = estado inicial

            gravandoSimulacao = true;
            // trava as duas caixas ate o fim: a simulacao inteira fica gravada com o mesmo modo de processamento
            chkGravarBanco.Enabled = false;
            chkParalelo.Enabled = false;
            AtualizarStatusBanco();
        }

        // Fecha o registro da simulacao no banco (UPDATE com status, data de fim e tempos)
        private void FinalizarGravacao(StatusSimulacao status)
        {
            if (!gravandoSimulacao || gravador == null) return; // nao tem simulacao aberta no banco: nada a fechar

            double? tempoEquilibrio = status == StatusSimulacao.EQUILIBRIO ? simulador.TempoSimulado : null; // so tem tempo de equilibrio se chegou nele (senao vai NULL)
            gravador.Enviar(new CmdFinalizarSimulacao(status, tempoEquilibrio, tempoProcessamentoTotalMs, DateTime.Now));

            gravandoSimulacao = false;
            chkGravarBanco.Enabled = true; // destrava as caixas para a proxima simulacao
            chkParalelo.Enabled = true;
        }

        // Chamado quando a thread do banco falhou (ex.: servidor caiu): desliga a gravacao e avisa o usuario
        private void TratarErroBanco()
        {
            string erro = gravador?.Erro ?? "erro desconhecido"; // a mensagem que a thread do banco guardou
            gravandoSimulacao = false;
            gravador = null; // a thread do banco ja terminou; uma nova sera criada se o usuario tentar de novo
            chkGravarBanco.Checked = false;
            chkGravarBanco.Enabled = true;
            chkParalelo.Enabled = true;
            AtualizarStatusBanco();

            // pausa o timer enquanto a mensagem esta aberta: o MessageBox continua processando os eventos da
            // janela, e sem isso o timer1_Tick rodaria de novo "por baixo" da mensagem
            bool estavaRodando = timer1.Enabled;
            timer1.Stop();
            MessageBox.Show("Erro ao gravar no banco:\n" + erro + "\n\nA gravação foi desligada e a simulação continua.");
            if (estavaRodando) timer1.Start();
        }

        // Mostra no painel: modo de processamento (paralelo/sequencial e a grade de blocos), ms por tick e andamento do banco
        private void AtualizarStatusBanco()
        {
            string modo = "-"; // antes de criar a matriz nao tem modo para mostrar
            if (simulador != null)
            {
                string grade = simulador.BlocosNaVertical + "x" + simulador.BlocosNaHorizontal;
                modo = simulador.UsarParalelismo && simulador.QuantidadeBlocos > 1
                    ? "Paralelo, " + simulador.QuantidadeBlocos + " blocos (" + grade + ")"
                    : "Sequencial, " + simulador.QuantidadeBlocos + " bloco(s) um por vez";
            }

            if (Reprisando && resumoReprise != null) // na reprise nao tem processamento: so leitura do banco
            {
                lblBanco.Text = "REPRISE (sem cálculo, lendo o banco)\n"
                              + "Simulação #" + resumoReprise.Id + " - " + resumoReprise.N + "x" + resumoReprise.N + "\n"
                              + "Iteração " + iteracaoReprise + " de " + resumoReprise.UltimaIteracao;
                return;
            }

            string banco;
            if (gravandoSimulacao && gravador != null)
                banco = "Banco: gravando simulação #" + (gravador.IdSimulacaoAtual > 0 ? gravador.IdSimulacaoAtual.ToString() : "...") + "\n"
                      + "Iterações gravadas: " + gravador.IteracoesGravadas + " | fila: " + gravador.Pendentes;
            else if (chkGravarBanco.Checked)
                banco = "Banco: grava ao clicar em Iniciar";
            else
                banco = "Banco: gravação desligada";

            lblBanco.Text = modo + "\n"
                          + "Processamento: " + ultimaDuracaoMs.ToString("F1") + " ms/tick\n"
                          + banco;
        }

        // ===================== REPRISE =====================
        // Mostra de novo, iteracao por iteracao, uma simulacao que foi gravada no banco.
        // NAO usa o processador para calcular: nao chama Passo(), nao usa Parallel.Invoke, nao aplica a Lei de Fourier.
        // Cada quadro e so: SELECT da iteracao -> copia os valores para os cubos (CarregarEstado) -> redesenha.
        // Por isso a tela mostra EXATAMENTE os numeros calculados na simulacao original.

        private async void btnReprise_Click(object sender, EventArgs e)
        {
            if (Reprisando) // o botao vira "Parar reprise" enquanto ela passa
            {
                PararReprise();
                return;
            }
            if (calculando) return; // um tick ainda esta calculando: espera ele terminar (milissegundos)

            // a tela vai mostrar a reprise no lugar da matriz atual, entao a simulacao atual e encerrada, igual ao "Criar Matriz"
            timer1.Stop();
            btnIniciar.Text = "Iniciar";
            FinalizarGravacao(StatusSimulacao.INTERROMPIDA);

            btnReprise.Enabled = false; // evita clique duplo enquanto conecta
            Cursor = Cursors.WaitCursor;
            LeitorBanco? leitor = null;
            ResumoSimulacao? escolhida = null;
            try
            {
                await EsperarFilaDoBanco(); // se ainda ha iteracoes na fila de gravacao, espera elas chegarem ao banco
                leitor = await LeitorBanco.AbrirAsync(ConfiguracaoBanco.StringConexao);
                List<ResumoSimulacao> simulacoes = await leitor.ListarSimulacoesAsync();
                Cursor = Cursors.Default;

                if (simulacoes.Count == 0)
                {
                    MessageBox.Show("Nenhuma simulação gravada no banco ainda.\nMarque \"Gravar no banco MySQL\" e clique em Iniciar para gravar uma.");
                }
                else
                {
                    using FormEscolherSimulacao janela = new FormEscolherSimulacao(simulacoes);
                    if (janela.ShowDialog(this) == DialogResult.OK) // ShowDialog: espera o usuario escolher
                        escolhida = janela.Escolhida;
                }
            }
            catch (Exception ex) // servidor desligado, senha errada, tabela inexistente...
            {
                Cursor = Cursors.Default;
                MessageBox.Show("Não foi possível ler o banco:\n" + ex.Message + "\n\nConfira a string em ConfiguracaoBanco.cs.");
            }
            finally
            {
                btnReprise.Enabled = true;
            }

            if (escolhida == null || leitor == null || fechando) // cancelou, deu erro ou a janela fechou
            {
                if (leitor != null) await leitor.DisposeAsync();
                return;
            }

            await ReproduzirAsync(leitor, escolhida); // a reprise e dona do leitor e fecha ele no final
        }

        private void PararReprise()
        {
            cancelarReprise?.Cancel(); // so pede para parar: o ReproduzirAsync percebe e encerra sozinho
        }

        private async Task ReproduzirAsync(LeitorBanco leitor, ResumoSimulacao resumo)
        {
            CancellationTokenSource cancelar = new CancellationTokenSource();
            cancelarReprise = cancelar;
            resumoReprise = resumo;
            iteracaoReprise = 0;
            btnReprise.Text = "Parar reprise";
            btnAplicar.Enabled = false;

            // matriz vazia so com o tamanho certo: os valores de verdade vem do banco a cada quadro.
            // O material, lado e temperatura daqui sao provisorios (sao trocados ja na iteracao 0)
            Simulador sim = new Simulador(resumo.N, Material.Todos[0], 0.1, 300);
            sim.UsarParalelismo = resumo.ProcessamentoParalelo; // so para o painel mostrar o modo que foi usado na gravacao
            simulador = sim;
            temperaturasAleatorias = resumo.TemperaturasAleatorias;
            linhaSelecionada = -1;
            colunaSelecionada = -1;

            // ja pede a iteracao 0. A consulta roda enquanto o codigo segue (await so mais abaixo)
            Task<IteracaoRegistro> proxima = leitor.CarregarIteracaoAsync(resumo.Id, 0, resumo.N);
            double tempoAnterior = -1;
            try
            {
                for (int k = 0; k <= resumo.UltimaIteracao; k++)
                {
                    IteracaoRegistro atual = await proxima; // espera a iteracao k chegar do banco
                    if (cancelar.IsCancellationRequested) break; // parou enquanto ela carregava

                    // PRE-CARREGAMENTO: enquanto a iteracao k aparece na tela e o quadro espera,
                    // a k+1 ja vai sendo lida do banco. Assim a reprise nao "engasga" entre os quadros
                    if (k < resumo.UltimaIteracao)
                        proxima = leitor.CarregarIteracaoAsync(resumo.Id, k + 1, resumo.N);

                    sim.CarregarEstado(atual); // so copia os valores gravados para os cubos, nenhuma conta de calor
                    iteracaoReprise = k;

                    // a escala de cores segue a mesma regra da simulacao: e definida no inicio (iteracao 0)
                    // e nas edicoes do usuario (iteracao com o MESMO tempo simulado da anterior = "Aplicar no cubo")
                    if (k == 0 || atual.TempoSimulado == tempoAnterior)
                    {
                        escalaMin = sim.MenorTemperatura();
                        escalaMax = sim.MaiorTemperatura();
                    }
                    tempoAnterior = atual.TempoSimulado;

                    AtualizarInfo();
                    AtualizarStatus();
                    AtualizarStatusBanco();
                    picGrade.Invalidate(); // redesenha com as temperaturas da iteracao k

                    // mesma velocidade da simulacao: um quadro a cada intervalo do timer (50 ms)
                    await Task.Delay(timer1.Interval, cancelar.Token);
                }

                if (!cancelar.IsCancellationRequested && !fechando)
                {
                    string fim = resumo.TempoEquilibrio != null
                        ? "Equilíbrio térmico atingido em " + resumo.TempoEquilibrio.Value.ToString("N0") + " s simulados."
                        : "Situação gravada: " + resumo.Status + ".";
                    MessageBox.Show("Reprise da simulação #" + resumo.Id + " concluída (" + (resumo.UltimaIteracao + 1) + " iterações).\n" + fim);
                }
            }
            catch (OperationCanceledException)
            {
                // o Task.Delay e interrompido na hora quando o usuario para a reprise: nao e erro
            }
            catch (Exception ex) // ex.: conexao caiu no meio da reprise
            {
                if (!fechando) MessageBox.Show("Erro durante a reprise:\n" + ex.Message);
            }
            finally
            {
                // se parou no meio, ainda pode ter uma consulta da proxima iteracao em andamento.
                // Espera ela acabar antes de fechar a conexao (fechar com consulta ativa daria erro)
                try { await proxima; } catch { }
                await leitor.DisposeAsync();

                if (cancelarReprise == cancelar) // so limpa se nenhuma outra reprise comecou nesse meio tempo
                {
                    cancelarReprise = null;
                    resumoReprise = null;
                }
                cancelar.Dispose();

                if (!fechando)
                {
                    btnReprise.Text = "Reprisar do banco";
                    btnAplicar.Enabled = true;
                    if (simulador != null) // a matriz da ultima iteracao fica na tela: da para clicar nos cubos ou continuar simulando dela
                    {
                        AtualizarStatus();
                        AtualizarStatusBanco();
                    }
                }
            }
        }

        // Espera a thread do banco gravar o que ainda esta na fila (no maximo 60 s),
        // para a reprise conseguir mostrar ate a ultima iteracao da simulacao que acabou de ser encerrada
        private async Task EsperarFilaDoBanco()
        {
            if (gravador == null) return;
            DateTime limite = DateTime.Now.AddSeconds(60);
            while (gravador.Pendentes > 0 && gravador.Erro == null && DateTime.Now < limite)
                await Task.Delay(50); // espera sem travar a tela
        }

        // Ao fechar a janela: para a simulacao, fecha o registro no banco e espera a fila terminar de gravar
        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            fechando = true; // avisa o tick que estiver calculando para descartar o resultado
            PararReprise(); // a reprise para sozinha e fecha a conexao dela
            timer1.Stop();
            FinalizarGravacao(StatusSimulacao.INTERROMPIDA); // fechou antes do equilibrio

            if (gravador != null)
            {
                // espera a fila esvaziar para nao perder as ultimas iteracoes
                Cursor = Cursors.WaitCursor;
                bool gravouTudo = gravador.Encerrar(TimeSpan.FromSeconds(60));
                Cursor = Cursors.Default;

                if (!gravouTudo)
                    MessageBox.Show("Algumas iterações podem não ter sido gravadas no banco.\n" + (gravador.Erro ?? ""));
            }

            imagemGrade?.Dispose(); // libera a imagem usada no desenho das matrizes grandes
        }
    }
}

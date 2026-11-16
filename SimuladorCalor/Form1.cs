// DUPLA --->   João Vitor Moraes  & André Dalvi

using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace SimuladorCalor
{
    public partial class Form1 : Form
    {
        private Simulador simulador;
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

        public Form1()
        {
            InitializeComponent();
        }

        private void btnCriar_Click(object sender, EventArgs e)
        {
            timer1.Stop(); // se a simulacao estiver rodando e o usuario quiser uma nova matriz, o timer para
            btnIniciar.Text = "Iniciar";
            FinalizarGravacao(StatusSimulacao.INTERROMPIDA); // se a matriz anterior estava sendo gravada, fecha o registro dela

            int n = (int)numN.Value; // o valor  do NumericUpDown é decimal, o (int) converte pra inteiro, do jeito que o simuladorp precisa 
            double lado = (double)numLado.Value; // mesma coisa, mas agora convertendo para double
            Material material = (Material)cmbMaterial.SelectedItem; // a box guarda os itens como generico. o (Material) serve para puxar um material colocado na box 
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

            Corpo c = simulador.Corpos[linhaSelecionada, colunaSelecionada]; // pega o cubo selecionado
            c.Material = (Material)cmbMaterialCorpo.SelectedItem; // troca o material do cubo
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

            if (timer1.Enabled) // verifica se a simulacao ja esta rodfando
            {
                timer1.Stop(); // se estava rodando, pausa, e o botao diz Iniciar 
                btnIniciar.Text = "Iniciar";
            }
            else
            {
                // na primeira vez que inicia esta matriz, abre o registro no banco com o estado inicial (iteracao 0).
                // E aqui, e nao no "Criar Matriz", para que as edicoes feitas antes de iniciar facam parte do estado inicial
                if (!gravandoSimulacao && chkGravarBanco.Checked)
                    IniciarGravacao();

                timer1.Start();
                btnIniciar.Text = "Pausar"; // se estiver parado, aciona o botao, e ele diz Pausar  
            }
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            foreach (Material m in Material.Todos)
            {
                cmbMaterial.Items.Add(m);
                cmbMaterialCorpo.Items.Add(m);
            }
            cmbMaterial.SelectedIndex = 0;
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
            numTempCorpo.Value = (decimal)Math.Min(5000, c.Temperatura); // mostra a temperatura atual

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

        private void IniciarGravacao()
        {
            if (gravador == null)
            {
                string? erro = GravadorBanco.TestarConexao(ConfiguracaoBanco.StringConexao);
                if (erro != null)
                {
                    chkGravarBanco.Checked = false;
                    MessageBox.Show("Não foi possível conectar ao MySQL:\n" + erro +
                                    "\n\nA simulação vai rodar sem gravar. Confira a string em ConfiguracaoBanco.cs.");
                    AtualizarStatusBanco();
                    return;
                }
                gravador = new GravadorBanco(ConfiguracaoBanco.StringConexao);
            }

            simulador.UsarParalelismo = chkParalelo.Checked;
            numeroIteracao = 0;
            tempoProcessamentoTotalMs = 0;

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

        private void FinalizarGravacao(StatusSimulacao status)
        {
            if (!gravandoSimulacao || gravador == null) return;

            double? tempoEquilibrio = status == StatusSimulacao.EQUILIBRIO ? simulador.TempoSimulado : null;
            gravador.Enviar(new CmdFinalizarSimulacao(status, tempoEquilibrio, tempoProcessamentoTotalMs, DateTime.Now));

            gravandoSimulacao = false;
            chkGravarBanco.Enabled = true;
            chkParalelo.Enabled = true;
        }

        private void TratarErroBanco()
        {
            string erro = gravador?.Erro ?? "erro desconhecido";
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

        private void AtualizarStatusBanco()
        {
            string modo = "-";
            if (simulador != null)
            {
                string grade = simulador.BlocosNaVertical + "x" + simulador.BlocosNaHorizontal;
                modo = simulador.UsarParalelismo && simulador.QuantidadeBlocos > 1
                    ? "Paralelo, " + simulador.QuantidadeBlocos + " blocos (" + grade + ")"
                    : "Sequencial, " + simulador.QuantidadeBlocos + " bloco(s) um por vez";
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

        private void Form1_FormClosing(object? sender, FormClosingEventArgs e)
        {
            fechando = true;
            timer1.Stop();
            FinalizarGravacao(StatusSimulacao.INTERROMPIDA);

            if (gravador != null)
            {
                // espera a fila esvaziar para nao perder as ultimas iteracoes
                Cursor = Cursors.WaitCursor;
                bool gravouTudo = gravador.Encerrar(TimeSpan.FromSeconds(60));
                Cursor = Cursors.Default;

                if (!gravouTudo)
                    MessageBox.Show("Algumas iterações podem não ter sido gravadas no banco.\n" + (gravador.Erro ?? ""));
            }

            imagemGrade?.Dispose();
        }
    }
}

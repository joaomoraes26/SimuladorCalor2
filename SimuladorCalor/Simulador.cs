using System;
using System.Threading.Tasks;

namespace SimuladorCalor
{
    public class Simulador
    {
        public Corpo[,] Corpos { get; private set; } // a matriz de cubos 
        public int N { get; private set; } // o tamanho da matriz (N linhas e N coluans)
        public double TempoSimulado { get; private set; } // tempo da simulação em segundos 

        // ===================== DECOMPOSICAO EM BLOCOS =====================
        // A matriz e dividida em uma GRADE DE BLOCOS, um bloco por nucleo do processador.
        // Exemplo: matriz 200x200 em um PC com 8 nucleos -> grade de 2 x 4 blocos (100 x 50 cubos cada).
        //
        //   +---------+---------+---------+---------+
        //   | bloco 0 | bloco 1 | bloco 2 | bloco 3 |
        //   +---------+---------+---------+---------+   <- fronteira
        //   | bloco 4 | bloco 5 | bloco 6 | bloco 7 |
        //   +---------+---------+---------+---------+
        //
        // Cada passo tem 3 fases:
        //   FASE 1 (paralela):   cada bloco calcula os pares INTERNOS (os dois cubos dentro do bloco)
        //   FASE 2 (sequencial): calcula os pares da FRONTEIRA (um cubo de cada lado do corte)
        //   FASE 3 (paralela):   cada bloco aplica a energia acumulada nos seus cubos

        public bool UsarParalelismo { get; set; } = true; // false = executa os mesmos blocos, um depois do outro
        public int BlocosNaVertical { get; private set; } // quantos blocos de cima para baixo
        public int BlocosNaHorizontal { get; private set; } // quantos blocos da esquerda para a direita
        public int QuantidadeBlocos => BlocosNaVertical * BlocosNaHorizontal;

        private readonly double[,] energia; // energia que cada cubo ganha/perde no passo (J)
        private readonly int[] limitesLinhas;  // linha onde cada faixa de blocos comeca (ultimo item = N)
        private readonly int[] limitesColunas; // coluna onde cada faixa de blocos comeca (ultimo item = N)
        private readonly Action[] acoesParesInternos; // fase 1: uma Action por bloco
        private readonly Action[] acoesAplicar;       // fase 3: uma Action por bloco
        private readonly ParallelOptions opcoesParalelas = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount // no maximo uma thread por nucleo logico
        };
        private double dtAtual; // o dt do passo atual, lido pelas Actions (que sao criadas uma vez so)

        public Simulador(int n, Material material, double lado, double temperatura) // cria a matriz com todos os cubos iguais 
        {
            N = n; // guarda o tamanho 
            Corpos = new Corpo[n, n]; // cria a matriz vazia 
            for (int i = 0; i < n; i++) // um percorre as linhas e outro as colunas 
            {
                for (int j = 0; j < n; j++) 
                {
                    Corpos[i, j] = new Corpo(material, lado, temperatura); // cria um cubo em cada posicao 
                }
            }

            energia = new double[n, n];

            // 1) decide o formato da grade de blocos a partir da quantidade de nucleos
            (int vertical, int horizontal) = CalcularGradeDeBlocos(Environment.ProcessorCount, n);
            BlocosNaVertical = vertical;
            BlocosNaHorizontal = horizontal;

            // 2) calcula onde cada bloco comeca e termina.
            //    A divisao inteira reparte as linhas o mais igual possivel (ex.: 200 / 3 -> 66, 67, 67)
            limitesLinhas = new int[vertical + 1];
            for (int k = 0; k <= vertical; k++)
                limitesLinhas[k] = k * n / vertical;

            limitesColunas = new int[horizontal + 1];
            for (int k = 0; k <= horizontal; k++)
                limitesColunas[k] = k * n / horizontal;

            // 3) cria as Actions de cada bloco. As variaveis locais (linhaInicio...) sao "capturadas"
            //    pela lambda: cada Action lembra qual e o seu bloco
            acoesParesInternos = new Action[QuantidadeBlocos];
            acoesAplicar = new Action[QuantidadeBlocos];
            int b = 0;
            for (int bv = 0; bv < vertical; bv++)
            {
                for (int bh = 0; bh < horizontal; bh++)
                {
                    int linhaInicio = limitesLinhas[bv], linhaFim = limitesLinhas[bv + 1];
                    int colunaInicio = limitesColunas[bh], colunaFim = limitesColunas[bh + 1];

                    acoesParesInternos[b] = () => CalcularParesInternos(linhaInicio, linhaFim, colunaInicio, colunaFim);
                    acoesAplicar[b] = () => AplicarEnergia(linhaInicio, linhaFim, colunaInicio, colunaFim);
                    b++;
                }
            }
        }

        // Escolhe a grade de blocos mais proxima de um quadrado, porque blocos quadrados tem MENOS fronteira.
        // 4 nucleos -> 2x2 | 6 -> 2x3 | 8 -> 2x4 | 12 -> 3x4 | 16 -> 4x4
        public static (int vertical, int horizontal) CalcularGradeDeBlocos(int nucleos, int n)
        {
            int vertical = 1;
            for (int d = (int)Math.Sqrt(nucleos); d >= 1; d--) // procura o maior divisor ate a raiz quadrada
            {
                if (nucleos % d == 0)
                {
                    vertical = d;
                    break;
                }
            }
            int horizontal = nucleos / vertical;

            // em matrizes pequenas (ex.: 2x2 com 8 nucleos) nao da para ter mais blocos que linhas/colunas
            return (Math.Min(vertical, n), Math.Min(horizontal, n));
        }

        public static double CalcularFluxo(Corpo a, Corpo b) // Lei de Fourrier,  retorna em W (Joules/segundo)
        {
            double k = Math.Min(a.Material.K, b.Material.K); // usa o menor k dos dois 
            double lado = Math.Min(a.Lado, b.Lado); // se os tamanhos sao diferentes, pega o lado do menor 
            double area = lado * lado; // A = L²
            double deltaT = a.Temperatura - b.Temperatura; // diferenca de temperatura entre os dois cubos em K 
            return k * area * deltaT; // o sinal define o sentido - Positivo: calor vai de A pra B.  Negativo de B pra A 
        }

        public void Passo(double dt) // avanca a simulacao em segundos (dt = Δt)
        {
            dtAtual = dt;

            // FASE 1 - pares internos de cada bloco.
            // O Parallel.Invoke entrega uma Action para cada thread e SO RETORNA quando todas terminarem
            // (funciona como uma barreira: a fase 2 nunca comeca com um bloco ainda calculando)
            if (UsarParalelismo && QuantidadeBlocos > 1)
                Parallel.Invoke(opcoesParalelas, acoesParesInternos);
            else
                foreach (Action acao in acoesParesInternos) acao(); // mesmos blocos, um depois do outro

            // FASE 2 - pares que cruzam a fronteira entre blocos (pouquissimos, por isso sequencial)
            CalcularParesDaFronteira(dt);

            // FASE 3 - aplica a energia. So agora as temperaturas mudam, entre as fases 1 e 2
            // todos os calculos usaram as temperaturas do inicio do passo (igual ao algoritmo original)
            if (UsarParalelismo && QuantidadeBlocos > 1)
                Parallel.Invoke(opcoesParalelas, acoesAplicar);
            else
                foreach (Action acao in acoesAplicar) acao();

            TempoSimulado += dt; // soma o tempo que durou o experimento 
        }

        // FASE 1 - mesmo algoritmo de pares da versao original, mas so dentro do bloco.
        // Como os DOIS cubos de um par interno estao no mesmo bloco, cada thread so escreve
        // na parte da matriz 'energia' que pertence ao seu bloco -> nao existe condicao de corrida.
        private void CalcularParesInternos(int linhaInicio, int linhaFim, int colunaInicio, int colunaFim)
        {
            double dt = dtAtual;

            // zera a energia do bloco (os valores do passo anterior ainda estao la)
            for (int i = linhaInicio; i < linhaFim; i++)
                for (int j = colunaInicio; j < colunaFim; j++)
                    energia[i, j] = 0;

            for (int i = linhaInicio; i < linhaFim; i++)
            {
                for (int j = colunaInicio; j < colunaFim; j++)
                {
                    if (j + 1 < colunaFim) // vizinho da direita, se ainda estiver dentro do bloco
                    {
                        double e = CalcularFluxo(Corpos[i, j], Corpos[i, j + 1]) * dt; // energia que passa do cubo atual para o da direita
                        energia[i, j] -= e; // um perde 
                        energia[i, j + 1] += e; // o outro ganha 
                    }
                    if (i + 1 < linhaFim) // vizinho de baixo, se ainda estiver dentro do bloco
                    {
                        double e = CalcularFluxo(Corpos[i, j], Corpos[i + 1, j]) * dt;
                        energia[i, j] -= e;
                        energia[i + 1, j] += e;
                    }
                }
            }
        }

        // FASE 2 - os pares que ficaram de fora da fase 1: um cubo de cada lado de um corte.
        // Roda depois que TODOS os blocos terminaram, entao nao disputa a matriz 'energia' com ninguem.
        private void CalcularParesDaFronteira(double dt)
        {
            // cortes verticais: entre a ultima coluna de um bloco e a primeira do bloco da direita
            for (int k = 1; k < BlocosNaHorizontal; k++)
            {
                int coluna = limitesColunas[k]; // primeira coluna do bloco da direita
                for (int i = 0; i < N; i++)
                {
                    double e = CalcularFluxo(Corpos[i, coluna - 1], Corpos[i, coluna]) * dt;
                    energia[i, coluna - 1] -= e;
                    energia[i, coluna] += e;
                }
            }

            // cortes horizontais: entre a ultima linha de um bloco e a primeira do bloco de baixo
            for (int k = 1; k < BlocosNaVertical; k++)
            {
                int linha = limitesLinhas[k]; // primeira linha do bloco de baixo
                for (int j = 0; j < N; j++)
                {
                    double e = CalcularFluxo(Corpos[linha - 1, j], Corpos[linha, j]) * dt;
                    energia[linha - 1, j] -= e;
                    energia[linha, j] += e;
                }
            }
        }

        // FASE 3 - cada bloco aplica a energia nos seus proprios cubos
        private void AplicarEnergia(int linhaInicio, int linhaFim, int colunaInicio, int colunaFim)
        {
            for (int i = linhaInicio; i < linhaFim; i++)
                for (int j = colunaInicio; j < colunaFim; j++)
                    Corpos[i, j].ReceberCalor(energia[i, j]);
        }

        // Tira uma "fotografia" de todos os cubos para gravar no banco.
        // Copia os valores para objetos imutaveis: a thread do banco pode le-los
        // enquanto a simulacao continua alterando os cubos, sem conflito.
        public IteracaoRegistro CriarRegistro(int numero)
        {
            EstadoCorpoRegistro[] estados = new EstadoCorpoRegistro[N * N];
            int k = 0;
            for (int i = 0; i < N; i++)
            {
                for (int j = 0; j < N; j++)
                {
                    Corpo c = Corpos[i, j];
                    estados[k++] = new EstadoCorpoRegistro(i, j, c.Material.Nome, c.Lado, c.Temperatura, c.Massa, c.CalorSensivel);
                }
            }
            return new IteracaoRegistro(numero, TempoSimulado, estados);
        }

        public double MenorTemperatura() // retorna a menor temperatura da matriz em K 
        {
            double menor = double.MaxValue; // comeca com o maior valor possivel 
            foreach (Corpo c in Corpos) // percorre todos os cubos da matriz 
                menor = Math.Min(menor, c.Temperatura); // compara e guarda sempre a menor encontrada 
            return menor; // retorna a menor temperatura 
        }

        public double MaiorTemperatura() // retorna a maior temperatura da matriz em K
        {
            double maior = double.MinValue; // comeca com o menor valor possivel 
            foreach (Corpo c in Corpos) // percorre todos os cubos da matriz 
                maior = Math.Max(maior, c.Temperatura); // compara e guarda a maior encontrada 
            return maior; // retorna a maior temperatura 
        }

        public double CalorTotal() // soma o calor sensivel de todos os cubos e devolve em Joules 
        {
            double total = 0; // comeca a soma com 0
            foreach (Corpo c in Corpos) // percorre todos os cubos 
                total += c.CalorSensivel; // soma o calor sensivel de cada cubo
            return total; // retorna o total 
        }
    }
}

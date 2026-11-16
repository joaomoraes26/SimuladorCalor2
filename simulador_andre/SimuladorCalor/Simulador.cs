using System;
using System.Threading.Tasks;

namespace SimuladorCalor
{
    // =====================================================================================
    //  SIMULADOR
    //  É o "motor" do programa. Guarda a matriz de cubos e sabe fazer o tempo andar:
    //  a cada passo, olha cada par de cubos vizinhos, calcula quanto calor passa de um
    //  para o outro e atualiza as temperaturas. A tela (Form1) só desenha o que está aqui
    //  dentro; o banco (GravadorBanco) só grava "fotos" do que está aqui dentro.
    //
    //  Para ficar rápido em matrizes grandes, a matriz é dividida em blocos e cada núcleo
    //  do processador cuida de um bloco ao mesmo tempo (computação paralela). Isso está
    //  explicado com calma logo abaixo, antes das variáveis dos blocos.
    // =====================================================================================
    public class Simulador
    {
        // A matriz de cubos em si. Corpos[i, j] é o cubo da linha i, coluna j.
        // Cada Corpo sabe o seu material, o tamanho do lado e a temperatura atual.
        public Corpo[,] Corpos { get; private set; }

        // Tamanho da matriz: ela sempre é quadrada, N linhas por N colunas (N² cubos no total).
        public int N { get; private set; }

        // Quanto tempo "de mentira" já passou dentro da simulação, em segundos.
        // Não tem nada a ver com o relógio do computador: se cada passo vale 1 s e rodamos
        // 500 passos, o TempoSimulado é 500 s, mesmo que o PC tenha feito isso em 0,1 s.
        public double TempoSimulado { get; private set; }

        // ===================== COMO A MATRIZ É DIVIDIDA (DECOMPOSIÇÃO EM BLOCOS) =====================
        // A ideia é simples: em vez de um núcleo só percorrer a matriz inteira, cortamos a matriz
        // em pedaços (blocos) e damos um pedaço para cada núcleo. Todos trabalham ao mesmo tempo.
        //
        // Exemplo: matriz 200x200 num PC com 8 núcleos vira uma grade de 2 x 4 blocos,
        // cada bloco com 100 x 50 cubos:
        //
        //   +---------+---------+---------+---------+
        //   | bloco 0 | bloco 1 | bloco 2 | bloco 3 |
        //   +---------+---------+---------+---------+   <- fronteira (corte entre blocos)
        //   | bloco 4 | bloco 5 | bloco 6 | bloco 7 |
        //   +---------+---------+---------+---------+
        //
        // O cuidado é com os cubos que ficam colados no corte: o par "último cubo do bloco 0 +
        // primeiro cubo do bloco 1" pertence aos dois blocos. Se as duas threads mexessem nele ao
        // mesmo tempo, uma poderia apagar a conta da outra (condição de corrida). Por isso cada
        // passo é feito em 3 fases:
        //
        //   FASE 1 (paralela):   cada bloco calcula só os pares que estão INTEIROS dentro dele
        //   FASE 2 (sequencial): uma thread só calcula os pares que atravessam o corte (fronteira)
        //   FASE 3 (paralela):   cada bloco aplica nos seus cubos a energia que foi somada

        // Liga/desliga o paralelismo (é a caixinha "Paralelo em blocos" da tela).
        // Desligado, os MESMOS blocos são executados, só que um depois do outro. Assim a única
        // diferença entre os dois modos é rodar ao mesmo tempo ou não, e dá para comparar o tempo de forma justa.
        public bool UsarParalelismo { get; set; } = true;

        // Formato da grade de blocos: quantas "fileiras" de blocos de cima para baixo e quantas
        // "colunas" de blocos da esquerda para a direita. No exemplo acima: 2 na vertical e 4 na horizontal.
        public int BlocosNaVertical { get; private set; }
        public int BlocosNaHorizontal { get; private set; }
        public int QuantidadeBlocos => BlocosNaVertical * BlocosNaHorizontal; // total de blocos (2 x 4 = 8)

        // Rascunho de energia do passo, uma posição para cada cubo (em Joules).
        // Durante as fases 1 e 2 a gente NÃO mexe nas temperaturas: só anota aqui quanto cada cubo
        // vai ganhar (positivo) ou perder (negativo). Só na fase 3 isso vira temperatura.
        // Isso garante que todas as contas do passo usam as temperaturas do começo do passo,
        // não importa qual bloco terminou primeiro.
        private readonly double[,] energia;

        // Onde cada faixa de blocos começa. Ex.: matriz 200 com 2 blocos na vertical -> {0, 100, 200}.
        // O bloco da faixa k vai da linha limitesLinhas[k] até limitesLinhas[k+1] - 1.
        // O último número é sempre N, para a última faixa saber onde termina.
        private readonly int[] limitesLinhas;
        private readonly int[] limitesColunas; // mesma coisa, só que para as colunas

        // O "trabalho" de cada bloco, já pronto para ser entregue às threads.
        // Cada Action é uma pequena função que já sabe qual é o seu bloco (veja o construtor).
        // Elas são criadas uma vez só, no construtor, e reaproveitadas em todos os passos.
        private readonly Action[] acoesParesInternos; // fase 1: uma por bloco
        private readonly Action[] acoesAplicar;       // fase 3: uma por bloco

        // Configuração do Parallel.Invoke: diz quantas threads, no máximo, podem rodar ao mesmo tempo
        // (uma por núcleo). Sem limite, o .NET poderia criar mais threads do que núcleos, e elas
        // ficariam disputando o processador em vez de ajudar.
        private readonly ParallelOptions opcoesParalelas;

        // O dt do passo que está sendo calculado. As Actions da fase 1 foram criadas no construtor,
        // antes de saber o dt, então elas leem o valor daqui.
        private double dtAtual;

        // ===================== CONSTRUTOR =====================
        // Cria a matriz N x N com todos os cubos iguais (mesmo material, lado e temperatura) e
        // já prepara toda a parte paralela: decide o formato da grade de blocos, calcula onde
        // cada bloco começa e termina e monta as Actions de cada bloco.
        //
        // 'nucleos' é opcional: 0 (o padrão) significa "use quantos núcleos este PC tiver".
        // A tela sempre usa o padrão; os testes passam outros valores para fingir que estão em
        // PCs com 1, 2, 4, 6, 8... núcleos.
        public Simulador(int n, Material material, double lado, double temperatura, int nucleos = 0)
        {
            if (nucleos <= 0) nucleos = Environment.ProcessorCount; // pergunta ao sistema quantos núcleos lógicos o processador tem
            opcoesParalelas = new ParallelOptions
            {
                MaxDegreeOfParallelism = nucleos // no máximo uma thread por núcleo
            };

            N = n;
            Corpos = new Corpo[n, n]; // por enquanto a matriz só tem posições vazias (null)
            for (int i = 0; i < n; i++) // percorre as linhas...
            {
                for (int j = 0; j < n; j++) // ...e, em cada linha, as colunas
                {
                    Corpos[i, j] = new Corpo(material, lado, temperatura); // coloca um cubo novo em cada posição
                }
            }

            energia = new double[n, n]; // o rascunho de energia, do mesmo tamanho da matriz

            // 1) Decide o formato da grade de blocos (ex.: 8 núcleos -> 2 x 4)
            (int vertical, int horizontal) = CalcularGradeDeBlocos(nucleos, n);
            BlocosNaVertical = vertical;
            BlocosNaHorizontal = horizontal;

            // 2) Calcula onde cada bloco começa e termina.
            //    k * n / vertical com divisão inteira reparte as linhas o mais igual possível,
            //    mesmo quando não dá divisão exata (ex.: 200 linhas em 3 faixas -> 66, 67 e 67 linhas).
            limitesLinhas = new int[vertical + 1];
            for (int k = 0; k <= vertical; k++)
                limitesLinhas[k] = k * n / vertical;

            limitesColunas = new int[horizontal + 1];
            for (int k = 0; k <= horizontal; k++)
                limitesColunas[k] = k * n / horizontal;

            // 3) Monta as Actions de cada bloco.
            //    O truque aqui é a lambda "() => ...": ela "captura" as variáveis linhaInicio, linhaFim,
            //    colunaInicio e colunaFim do momento em que foi criada. Como essas variáveis são declaradas
            //    DENTRO do laço, cada volta cria variáveis novas, e cada Action fica lembrando os limites
            //    do seu próprio bloco. Depois, para rodar o bloco 3, basta chamar acoesParesInternos[3]().
            acoesParesInternos = new Action[QuantidadeBlocos];
            acoesAplicar = new Action[QuantidadeBlocos];
            int b = 0; // número do bloco que está sendo montado (0, 1, 2...)
            for (int bv = 0; bv < vertical; bv++) // fileira de blocos
            {
                for (int bh = 0; bh < horizontal; bh++) // coluna de blocos
                {
                    int linhaInicio = limitesLinhas[bv], linhaFim = limitesLinhas[bv + 1];
                    int colunaInicio = limitesColunas[bh], colunaFim = limitesColunas[bh + 1];

                    acoesParesInternos[b] = () => CalcularParesInternos(linhaInicio, linhaFim, colunaInicio, colunaFim);
                    acoesAplicar[b] = () => AplicarEnergia(linhaInicio, linhaFim, colunaInicio, colunaFim);
                    b++;
                }
            }
        }

        // ===================== FORMATO DA GRADE DE BLOCOS =====================
        // Recebe quantos núcleos o PC tem e devolve como arrumar os blocos (vertical x horizontal).
        //
        // Por que tentar deixar o mais "quadrado" possível? Porque quanto mais quadrados os blocos,
        // menor a fronteira entre eles, e a fronteira é a parte que roda sem paralelismo (fase 2).
        // Com 8 núcleos, 2 x 4 tem menos corte do que 1 x 8 (que viraria 8 tiras finas).
        //
        // Como faz: procura o maior divisor do número de núcleos que seja menor ou igual à raiz
        // quadrada dele. Esse divisor vira a vertical e o resto da divisão vira a horizontal.
        //   4 núcleos -> 2x2 | 6 -> 2x3 | 8 -> 2x4 | 12 -> 3x4 | 16 -> 4x4 | 7 (primo) -> 1x7
        public static (int vertical, int horizontal) CalcularGradeDeBlocos(int nucleos, int n)
        {
            int vertical = 1; // se não achar nenhum divisor melhor, fica 1 (sempre divide)
            for (int d = (int)Math.Sqrt(nucleos); d >= 1; d--) // começa na raiz e vai descendo
            {
                if (nucleos % d == 0) // achou um divisor exato: é o maior possível até a raiz
                {
                    vertical = d;
                    break;
                }
            }
            int horizontal = nucleos / vertical;

            // Numa matriz pequena não dá para ter mais blocos do que linhas ou colunas
            // (ex.: matriz 2x2 com 8 núcleos: no máximo 2 blocos em cada direção, cada um com 1 cubo)
            return (Math.Min(vertical, n), Math.Min(horizontal, n));
        }

        // ===================== A FÍSICA: LEI DE FOURIER =====================
        // Calcula quanto calor passa por segundo do cubo A para o cubo B, que estão encostados
        // por uma face. Devolve em Watts (Joules por segundo).
        //
        //   fluxo = k . A . ΔT
        //
        //   k  = condutividade térmica: o quão fácil o calor atravessa o material.
        //        Se os materiais são diferentes, usamos o MENOR k, porque o calor é limitado
        //        pelo material que conduz pior (é o "gargalo" do caminho).
        //   A  = área da face de contato. Se os cubos têm tamanhos diferentes, o contato é a face do menor.
        //   ΔT = diferença de temperatura (A - B). É ela que "empurra" o calor.
        //
        // O sinal do resultado diz o sentido: positivo, o calor vai de A para B (A está mais quente);
        // negativo, vai de B para A. Se as temperaturas forem iguais, dá zero e nada acontece.
        //
        // É 'static' porque não depende da matriz: só dos dois cubos que recebe.
        public static double CalcularFluxo(Corpo a, Corpo b)
        {
            double k = Math.Min(a.Material.K, b.Material.K);
            double lado = Math.Min(a.Lado, b.Lado);
            double area = lado * lado; // A = L²
            double deltaT = a.Temperatura - b.Temperatura; // em Kelvin
            return k * area * deltaT;
        }

        // ===================== UM PASSO DA SIMULAÇÃO =====================
        // Faz o tempo andar 'dt' segundos. É o método que a tela chama várias vezes por atualização
        // (o campo "Velocidade" diz quantas). Cada chamada passa pelas 3 fases explicadas lá em cima.
        public void Passo(double dt)
        {
            dtAtual = dt; // deixa o dt onde as Actions da fase 1 conseguem ler

            // FASE 1 - cada bloco calcula os pares que estão inteiros dentro dele.
            // O Parallel.Invoke recebe a lista de Actions (uma por bloco), distribui entre as threads
            // e SÓ DEVOLVE O CONTROLE QUANDO TODAS TERMINAREM. Isso funciona como uma "barreira":
            // é impossível a fase 2 começar com algum bloco ainda no meio da conta.
            // Com um bloco só (PC de 1 núcleo) não vale a pena chamar o Parallel, então roda direto.
            if (UsarParalelismo && QuantidadeBlocos > 1)
                Parallel.Invoke(opcoesParalelas, acoesParesInternos);
            else
                foreach (Action acao in acoesParesInternos) acao(); // modo sequencial: um bloco depois do outro

            // FASE 2 - os pares que ficaram em cima dos cortes entre blocos.
            // São pouquíssimos (numa 200x200 com grade 2x4, cerca de 1% dos pares), então
            // fazer com uma thread só é rápido e evita qualquer briga pela matriz 'energia'.
            CalcularParesDaFronteira(dt);

            // FASE 3 - cada bloco transforma a energia anotada em temperatura nos seus cubos.
            // Só aqui as temperaturas mudam de verdade. Até agora, todas as contas usaram as
            // temperaturas do começo do passo, igual ao algoritmo original sem blocos.
            if (UsarParalelismo && QuantidadeBlocos > 1)
                Parallel.Invoke(opcoesParalelas, acoesAplicar);
            else
                foreach (Action acao in acoesAplicar) acao();

            TempoSimulado += dt; // mais um passo de tempo se passou dentro da simulação
        }

        // ===================== FASE 1: PARES INTERNOS DE UM BLOCO =====================
        // Recebe os limites de UM bloco e calcula a troca de calor entre os vizinhos que estão dentro dele.
        // Cada cubo olha só para o vizinho da DIREITA e o de BAIXO. Parece pouco, mas assim cada par é
        // calculado exatamente uma vez (o vizinho da esquerda já olhou para ele como "direita", e o de cima como "baixo").
        //
        // Por que várias threads podem rodar isto ao mesmo tempo sem problema: os dois cubos de um par
        // interno estão no mesmo bloco, então cada thread só escreve na parte da matriz 'energia' que é
        // do seu bloco. Nenhuma thread encosta na área da outra, e não precisa de lock.
        // Os pares em que o vizinho cai fora do bloco são pulados aqui e ficam para a fase 2.
        private void CalcularParesInternos(int linhaInicio, int linhaFim, int colunaInicio, int colunaFim)
        {
            double dt = dtAtual;

            // Limpa o rascunho de energia deste bloco, porque ainda tem os valores do passo anterior.
            // Cada bloco zera só a sua parte, então isso também pode rodar em paralelo.
            for (int i = linhaInicio; i < linhaFim; i++)
                for (int j = colunaInicio; j < colunaFim; j++)
                    energia[i, j] = 0;

            for (int i = linhaInicio; i < linhaFim; i++)
            {
                for (int j = colunaInicio; j < colunaFim; j++)
                {
                    if (j + 1 < colunaFim) // tem vizinho à direita DENTRO do bloco?
                    {
                        // energia que atravessa a face neste passo: fluxo (J/s) vezes a duração do passo (s)
                        double e = CalcularFluxo(Corpos[i, j], Corpos[i, j + 1]) * dt;
                        energia[i, j] -= e;     // o que sai de um...
                        energia[i, j + 1] += e; // ...entra no outro. Assim a energia total nunca muda (conservação)
                    }
                    if (i + 1 < linhaFim) // tem vizinho embaixo DENTRO do bloco?
                    {
                        double e = CalcularFluxo(Corpos[i, j], Corpos[i + 1, j]) * dt;
                        energia[i, j] -= e;
                        energia[i + 1, j] += e;
                    }
                }
            }
        }

        // ===================== FASE 2: PARES DA FRONTEIRA =====================
        // Calcula os pares que a fase 1 pulou: os que têm um cubo de cada lado de um corte entre blocos.
        // Roda numa thread só e só depois que todos os blocos terminaram, então pode somar na matriz
        // 'energia' de qualquer bloco sem risco de atrapalhar ninguém.
        private void CalcularParesDaFronteira(double dt)
        {
            // Cortes verticais (as linhas "|" do desenho): entre a última coluna de um bloco
            // e a primeira coluna do bloco da direita. Começa em k = 1 porque o limite 0 é a
            // borda esquerda da matriz, que não tem vizinho.
            for (int k = 1; k < BlocosNaHorizontal; k++)
            {
                int coluna = limitesColunas[k]; // primeira coluna do bloco da direita; a coluna - 1 é a última do bloco da esquerda
                for (int i = 0; i < N; i++) // o corte atravessa a matriz inteira de cima a baixo
                {
                    double e = CalcularFluxo(Corpos[i, coluna - 1], Corpos[i, coluna]) * dt;
                    energia[i, coluna - 1] -= e;
                    energia[i, coluna] += e;
                }
            }

            // Cortes horizontais (as linhas "-" do desenho): entre a última linha de um bloco
            // e a primeira linha do bloco de baixo
            for (int k = 1; k < BlocosNaVertical; k++)
            {
                int linha = limitesLinhas[k]; // primeira linha do bloco de baixo
                for (int j = 0; j < N; j++) // o corte atravessa a matriz inteira da esquerda à direita
                {
                    double e = CalcularFluxo(Corpos[linha - 1, j], Corpos[linha, j]) * dt;
                    energia[linha - 1, j] -= e;
                    energia[linha, j] += e;
                }
            }
        }

        // ===================== FASE 3: APLICAR A ENERGIA =====================
        // Para cada cubo do bloco, pega a energia total que ele ganhou ou perdeu no passo (somando todos
        // os vizinhos, de dentro e da fronteira) e transforma em mudança de temperatura.
        // Quem faz a conta é o Corpo.ReceberCalor, usando ΔT = Q / (m . c).
        // Cada thread só mexe nos cubos do próprio bloco, então também roda em paralelo sem conflito.
        private void AplicarEnergia(int linhaInicio, int linhaFim, int colunaInicio, int colunaFim)
        {
            for (int i = linhaInicio; i < linhaFim; i++)
                for (int j = colunaInicio; j < colunaFim; j++)
                    Corpos[i, j].ReceberCalor(energia[i, j]);
        }

        // ===================== "FOTO" PARA O BANCO =====================
        // Copia o estado de todos os cubos (posição, material, lado, temperatura, massa e calor sensível)
        // para objetos que não podem mais ser alterados (records). O 'numero' é o número da iteração.
        //
        // Por que copiar em vez de mandar os próprios cubos? Porque a gravação acontece em outra thread,
        // que pode demorar. Se ela lesse os cubos de verdade, a simulação poderia mudar as temperaturas
        // no meio da gravação e o banco ficaria com metade de uma iteração e metade de outra.
        // Com a cópia, a simulação segue livre e o banco grava com calma a foto daquele instante.
        public IteracaoRegistro CriarRegistro(int numero)
        {
            EstadoCorpoRegistro[] estados = new EstadoCorpoRegistro[N * N]; // um item por cubo, em uma lista "deitada"
            int k = 0; // posição na lista: anda de 0 até N² - 1 (linha por linha)
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

        // ===================== REPRISE: VOLTAR UMA FOTO PARA A MATRIZ =====================
        // É o caminho contrário do CriarRegistro: pega uma foto lida do banco e coloca os valores de volta nos cubos.
        // Não calcula nada: não chama Passo, não usa a Lei de Fourier, não cria threads. Só troca material,
        // lado e temperatura de cada cubo e o tempo simulado, para a tela desenhar exatamente o que foi gravado.
        public void CarregarEstado(IteracaoRegistro registro)
        {
            // Proteção: a foto precisa ter um cubo para cada posição da matriz. Se vier com
            // tamanho errado (ex.: foto de uma simulação 10x10 numa matriz 200x200), para aqui com uma mensagem clara.
            if (registro.Corpos.Length != N * N)
                throw new ArgumentException("A iteracao " + registro.Numero + " tem " + registro.Corpos.Length + " corpos, mas a matriz tem " + (N * N) + ".");

            foreach (EstadoCorpoRegistro estado in registro.Corpos)
            {
                Corpo c = Corpos[estado.Linha, estado.Coluna]; // a linha e a coluna gravadas dizem qual cubo atualizar
                c.Material = Material.BuscarPorNome(estado.Material); // o banco guarda o nome; aqui volta a ser o objeto Material
                c.Lado = estado.Lado;
                c.Temperatura = estado.Temperatura;
                // massa e calor sensível não precisam ser copiados: o Corpo recalcula sozinho a partir dos valores acima
            }
            TempoSimulado = registro.TempoSimulado; // o tempo também vem pronto do banco, não é somado
        }

        // ===================== RESUMOS DA MATRIZ (usados pela tela) =====================

        // Menor temperatura entre todos os cubos, em K. Junto com a maior, serve para montar a escala de
        // cores (azul = mais frio, vermelho = mais quente) e para saber se chegou ao equilíbrio.
        public double MenorTemperatura()
        {
            double menor = double.MaxValue; // começa com o maior número possível, assim qualquer cubo vai ser menor que ele
            foreach (Corpo c in Corpos) // o foreach numa matriz [,] passa por todos os cubos, linha por linha
                menor = Math.Min(menor, c.Temperatura); // fica sempre com a menor encontrada até agora
            return menor;
        }

        // Maior temperatura entre todos os cubos, em K. A tela considera que chegou ao equilíbrio
        // térmico quando (maior - menor) fica abaixo de 0,01 K.
        public double MaiorTemperatura()
        {
            double maior = double.MinValue; // começa com o menor número possível, assim qualquer cubo vai ser maior que ele
            foreach (Corpo c in Corpos)
                maior = Math.Max(maior, c.Temperatura); // fica sempre com a maior encontrada até agora
            return maior;
        }

        // Soma do calor sensível (Q = m . c . T) de todos os cubos, em Joules.
        // É a "prova real" da simulação: como todo calor que um cubo perde vai para o vizinho,
        // esse total tem que ficar praticamente constante o tempo todo. Só muda quando o usuário
        // edita um cubo na mão (porque aí entra ou sai energia de fora).
        public double CalorTotal()
        {
            double total = 0;
            foreach (Corpo c in Corpos)
                total += c.CalorSensivel;
            return total;
        }
    }
}

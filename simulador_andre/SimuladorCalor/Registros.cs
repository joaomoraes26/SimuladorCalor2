using System;

namespace SimuladorCalor
{
    // Situacao final de uma simulacao (gravada como texto na coluna simulacao.status)
    public enum StatusSimulacao
    {
        EM_ANDAMENTO,
        EQUILIBRIO,  // atingiu equilibrio termico
        INTERROMPIDA // o usuario criou outra matriz ou fechou o programa antes do equilibrio
    }

    // "Fotografia" de um cubo. 'record' = objeto imutavel: depois de criado ninguem altera,
    // entao e seguro ler de outra thread (a do banco) enquanto a simulacao continua.
    public sealed record EstadoCorpoRegistro(
        int Linha,
        int Coluna,
        string Material,
        double Lado,
        double Temperatura,
        double Massa,
        double CalorSensivel);

    // "Fotografia" da matriz inteira em uma iteracao
    public sealed record IteracaoRegistro(
        int Numero,
        double TempoSimulado,
        EstadoCorpoRegistro[] Corpos);

    // ===== Comandos enviados da tela para o gravador (fila produtor/consumidor) =====
    public abstract record ComandoBanco;

    public sealed record CmdNovaSimulacao(
        int N,
        double Dt,
        bool TemperaturasAleatorias,
        bool ProcessamentoParalelo,
        int QuantidadeBlocos,
        DateTime DataInicio,
        IteracaoRegistro Inicial) : ComandoBanco;

    public sealed record CmdSalvarIteracao(IteracaoRegistro Iteracao) : ComandoBanco;

    public sealed record CmdFinalizarSimulacao(
        StatusSimulacao Status,
        double? TempoEquilibrio,
        double TempoProcessamentoMs,
        DateTime DataFim) : ComandoBanco;

    // ===== Reprise: resumo de uma simulacao gravada (uma linha da tabela simulacao) =====
    public sealed record ResumoSimulacao(
        int Id,
        int N,
        bool TemperaturasAleatorias,
        bool ProcessamentoParalelo,
        int QuantidadeBlocos,
        string Status,
        DateTime DataInicio,
        double? TempoEquilibrio,
        int UltimaIteracao) // maior numero de iteracao gravado (as iteracoes vao de 0 ate ela)
    {
        // texto mostrado na lista da janela de escolha
        public override string ToString()
        {
            string modo = ProcessamentoParalelo ? "paralelo " + QuantidadeBlocos + " blocos" : "sequencial";
            return "#" + Id + "  |  " + N + "x" + N + "  |  " + Status + "  |  " + (UltimaIteracao + 1) + " iterações  |  "
                 + modo + "  |  " + DataInicio.ToString("dd/MM/yyyy HH:mm");
        }
    }
}

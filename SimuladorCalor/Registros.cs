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
}

namespace SimuladorCalor
{
    public static class ConfiguracaoBanco
    {
        // AJUSTE AQUI conforme o seu MySQL (usuario, senha, porta).
        // Connection Timeout curto para a tela nao ficar travada se o servidor estiver desligado.
        public const string StringConexao =
            "Server=localhost;Port=3306;Database=simulador_calor;User ID=root;Password=;Connection Timeout=5;";

        // Se o banco ficar mais lento que a simulacao, a fila cresce. Acima deste limite a simulacao
        // espera o banco alcancar (nenhuma iteracao e perdida). Em uma matriz 200x200 cada iteracao
        // na fila ocupa alguns MB de memoria, por isso o limite e pequeno.
        public const int LimiteIteracoesPendentes = 10;

        // Quantos corpos vao em cada INSERT. 40.000 corpos (200x200) viram 40 comandos de 1.000 linhas,
        // todos na mesma transacao. Um INSERT unico com 40.000 linhas poderia passar do tamanho
        // maximo de pacote do MySQL (max_allowed_packet).
        public const int CorposPorInsert = 1000;
    }
}

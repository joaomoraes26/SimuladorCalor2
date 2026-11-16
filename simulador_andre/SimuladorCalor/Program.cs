namespace SimuladorCalor
{
    internal static class Program
    {
        // Ponto de entrada: e o primeiro metodo executado quando o programa abre
        [STAThread] // a tela do Windows Forms precisa rodar em uma thread "STA" (exigencia do Windows para janelas)
        static void Main()
        {
            ApplicationConfiguration.Initialize(); // liga as configuracoes padrao (estilo visual, DPI, fonte)
            Application.Run(new Form1()); // abre a janela principal e so retorna quando ela for fechada
        }
    }
}

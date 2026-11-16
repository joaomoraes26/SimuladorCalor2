namespace SimuladorCalor
{
    // Janela pequena que lista as simulacoes gravadas no banco para o usuario escolher qual reprisar.
    // Montada direto no codigo (sem Designer) porque e so uma lista e dois botoes
    public class FormEscolherSimulacao : Form
    {
        private readonly ListBox lstSimulacoes = new ListBox();

        public ResumoSimulacao? Escolhida => lstSimulacoes.SelectedItem as ResumoSimulacao; // null se nada foi escolhido

        public FormEscolherSimulacao(List<ResumoSimulacao> simulacoes)
        {
            Text = "Reprisar simulação gravada";
            ClientSize = new Size(760, 360);
            StartPosition = FormStartPosition.CenterParent; // abre no meio da janela principal
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;

            Label lblAjuda = new Label
            {
                Text = "Escolha a simulação. A reprise só lê o banco e redesenha cada iteração gravada (nenhum cálculo é refeito).",
                Location = new Point(12, 10),
                Size = new Size(736, 20)
            };

            lstSimulacoes.Location = new Point(12, 34);
            lstSimulacoes.Size = new Size(736, 280);
            lstSimulacoes.Font = new Font("Consolas", 9.5f); // fonte de largura fixa: as colunas do texto ficam alinhadas
            foreach (ResumoSimulacao s in simulacoes)
                lstSimulacoes.Items.Add(s); // a lista mostra o ToString do ResumoSimulacao
            if (lstSimulacoes.Items.Count > 0) lstSimulacoes.SelectedIndex = 0; // a mais recente ja vem selecionada
            lstSimulacoes.DoubleClick += (s, e) => { if (Escolhida != null) DialogResult = DialogResult.OK; }; // clique duplo = Reprisar

            Button btnOk = new Button { Text = "Reprisar", Location = new Point(560, 322), Size = new Size(90, 26), DialogResult = DialogResult.OK };
            Button btnCancelar = new Button { Text = "Cancelar", Location = new Point(658, 322), Size = new Size(90, 26), DialogResult = DialogResult.Cancel };
            AcceptButton = btnOk;      // Enter = Reprisar
            CancelButton = btnCancelar; // Esc = Cancelar

            Controls.Add(lblAjuda);
            Controls.Add(lstSimulacoes);
            Controls.Add(btnOk);
            Controls.Add(btnCancelar);
        }
    }
}

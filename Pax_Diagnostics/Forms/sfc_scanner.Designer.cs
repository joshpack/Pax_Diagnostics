namespace Pax_Diagnostics.Forms
{
    partial class sfc_scanner
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            runSfcButton = new Button();
            richTextBox1 = new RichTextBox();
            statusLabel = new Label();
            selectLogButton = new Button();
            SuspendLayout();
            // 
            // runSfcButton
            // 
            runSfcButton.Location = new Point(12, 12);
            runSfcButton.Name = "runSfcButton";
            runSfcButton.Size = new Size(113, 23);
            runSfcButton.TabIndex = 0;
            runSfcButton.Text = "Run local scan";
            runSfcButton.UseVisualStyleBackColor = true;
            runSfcButton.Click += RunSfcButton_Click;
            // 
            // richTextBox1
            // 
            richTextBox1.Font = new Font("Consolas", 9.75F, FontStyle.Regular, GraphicsUnit.Point, 0);
            richTextBox1.Location = new Point(12, 41);
            richTextBox1.Name = "richTextBox1";
            richTextBox1.Size = new Size(776, 397);
            richTextBox1.TabIndex = 1;
            richTextBox1.Text = "";
            // 
            // statusLabel
            // 
            statusLabel.Location = new Point(329, 9);
            statusLabel.Name = "statusLabel";
            statusLabel.Size = new Size(459, 29);
            statusLabel.TabIndex = 2;
            statusLabel.TextAlign = ContentAlignment.MiddleRight;
            // 
            // selectLogButton
            // 
            selectLogButton.Location = new Point(147, 12);
            selectLogButton.Name = "selectLogButton";
            selectLogButton.Size = new Size(113, 23);
            selectLogButton.TabIndex = 3;
            selectLogButton.Text = "Open file";
            selectLogButton.UseVisualStyleBackColor = true;
            selectLogButton.Click += selectLogButton_Click_1;
            // 
            // sfc_scanner
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            Controls.Add(selectLogButton);
            Controls.Add(statusLabel);
            Controls.Add(richTextBox1);
            Controls.Add(runSfcButton);
            Name = "sfc_scanner";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "sfc_scanner";
            ResumeLayout(false);
        }

        #endregion

        private Button runSfcButton;
        private RichTextBox richTextBox1;
        private Label statusLabel;
        private Button selectLogButton;
    }
}
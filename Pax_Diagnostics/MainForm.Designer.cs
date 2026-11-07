namespace Pax_Diagnostics
{
    partial class MainForm
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
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
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            menuStrip1 = new MenuStrip();
            fileToolStripMenuItem = new ToolStripMenuItem();
            toolsToolStripMenuItem = new ToolStripMenuItem();
            sfcscannowToolStripMenuItem = new ToolStripMenuItem();
            windowsReliabilityCenterToolStripMenuItem = new ToolStripMenuItem();
            osLabel = new Label();
            label1 = new Label();
            menuStrip1.SuspendLayout();
            SuspendLayout();
            // 
            // menuStrip1
            // 
            menuStrip1.Items.AddRange(new ToolStripItem[] { fileToolStripMenuItem, toolsToolStripMenuItem });
            menuStrip1.Location = new Point(0, 0);
            menuStrip1.Name = "menuStrip1";
            menuStrip1.Size = new Size(800, 24);
            menuStrip1.TabIndex = 0;
            menuStrip1.Text = "menuStrip1";
            // 
            // fileToolStripMenuItem
            // 
            fileToolStripMenuItem.Name = "fileToolStripMenuItem";
            fileToolStripMenuItem.Size = new Size(37, 20);
            fileToolStripMenuItem.Text = "&File";
            // 
            // toolsToolStripMenuItem
            // 
            toolsToolStripMenuItem.DropDownItems.AddRange(new ToolStripItem[] { sfcscannowToolStripMenuItem, windowsReliabilityCenterToolStripMenuItem });
            toolsToolStripMenuItem.Name = "toolsToolStripMenuItem";
            toolsToolStripMenuItem.Size = new Size(47, 20);
            toolsToolStripMenuItem.Text = "&Tools";
            // 
            // sfcscannowToolStripMenuItem
            // 
            sfcscannowToolStripMenuItem.Name = "sfcscannowToolStripMenuItem";
            sfcscannowToolStripMenuItem.Size = new Size(215, 22);
            sfcscannowToolStripMenuItem.Text = "&sfc /scannow";
            sfcscannowToolStripMenuItem.Click += sfcscannowToolStripMenuItem_Click;
            // 
            // windowsReliabilityCenterToolStripMenuItem
            // 
            windowsReliabilityCenterToolStripMenuItem.Name = "windowsReliabilityCenterToolStripMenuItem";
            windowsReliabilityCenterToolStripMenuItem.Size = new Size(215, 22);
            windowsReliabilityCenterToolStripMenuItem.Text = "&Windows Reliability Center";
            windowsReliabilityCenterToolStripMenuItem.Click += windowsReliabilityCenterToolStripMenuItem_Click;
            // 
            // osLabel
            // 
            osLabel.AutoSize = true;
            osLabel.Font = new Font("Consolas", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            osLabel.Location = new Point(31, 61);
            osLabel.Name = "osLabel";
            osLabel.Size = new Size(36, 19);
            osLabel.TabIndex = 1;
            osLabel.Text = "OS:";
            // 
            // label1
            // 
            label1.AutoSize = true;
            label1.Font = new Font("Consolas", 12F, FontStyle.Regular, GraphicsUnit.Point, 0);
            label1.Location = new Point(382, 216);
            label1.Name = "label1";
            label1.Size = new Size(36, 19);
            label1.TabIndex = 2;
            label1.Text = "OS:";
            // 
            // MainForm
            // 
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(800, 450);
            Controls.Add(label1);
            Controls.Add(osLabel);
            Controls.Add(menuStrip1);
            MainMenuStrip = menuStrip1;
            Name = "MainForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "Form1";
            menuStrip1.ResumeLayout(false);
            menuStrip1.PerformLayout();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion

        private MenuStrip menuStrip1;
        private ToolStripMenuItem fileToolStripMenuItem;
        private ToolStripMenuItem toolsToolStripMenuItem;
        private ToolStripMenuItem sfcscannowToolStripMenuItem;
        private ToolStripMenuItem windowsReliabilityCenterToolStripMenuItem;
        private Label osLabel;
        private Label label1;
    }
}

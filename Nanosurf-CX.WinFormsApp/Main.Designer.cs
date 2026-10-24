namespace Nanosurf_CX.WinFormsApp
{
    partial class Main
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
            this.btnOpen = new Button();
            this.btnClose = new Button();
            this.btnListen = new Button();
            this.SuspendLayout();
            // 
            // btnOpen
            // 
            this.btnOpen.Location = new Point(335, 275);
            this.btnOpen.Name = "btnOpen";
            this.btnOpen.Size = new Size(120, 50);
            this.btnOpen.TabIndex = 0;
            this.btnOpen.Text = "Open CX";
            this.btnOpen.UseVisualStyleBackColor = true;
            // 
            // btnClose
            // 
            this.btnClose.Location = new Point(509, 275);
            this.btnClose.Name = "btnClose";
            this.btnClose.Size = new Size(120, 50);
            this.btnClose.TabIndex = 1;
            this.btnClose.Text = "Close CX";
            this.btnClose.UseVisualStyleBackColor = true;
            // 
            // btnListen
            // 
            this.btnListen.Location = new Point(687, 275);
            this.btnListen.Name = "btnListen";
            this.btnListen.Size = new Size(120, 50);
            this.btnListen.TabIndex = 2;
            this.btnListen.Text = "Listen CX";
            this.btnListen.UseVisualStyleBackColor = true;
            // 
            // Main
            // 
            this.AutoScaleDimensions = new SizeF(9F, 20F);
            this.AutoScaleMode = AutoScaleMode.Font;
            this.ClientSize = new Size(1214, 755);
            this.Controls.Add(this.btnListen);
            this.Controls.Add(this.btnClose);
            this.Controls.Add(this.btnOpen);
            this.Name = "Main";
            this.Text = "Main";
            this.ResumeLayout(false);
        }

        #endregion

        private Button btnOpen;
        private Button btnClose;
        private Button btnListen;
    }
}
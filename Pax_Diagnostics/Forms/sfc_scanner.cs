using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Window;

namespace Pax_Diagnostics.Forms
{
    public partial class sfc_scanner : Form
    {
        private string errorLogPath;
        public sfc_scanner()
        {
            InitializeComponent();
            errorLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "SFC_ErrorLog.txt");
        }

        private async void RunSfcButton_Click(object sender, EventArgs e)
        {
            try
            {
                statusLabel.Text = "Starting sfc /scannow...";
                runSfcButton.Enabled = false;
                selectLogButton.Enabled = false;
                richTextBox1.Clear();
                File.AppendAllText(errorLogPath, $"{DateTime.Now}: Starting sfc /scannow...\n");
                Application.DoEvents();

                // Run sfc /scannow
                bool sfcSuccess = await RunSfcScannow();

                if (!sfcSuccess)
                {
                    runSfcButton.Enabled = true;
                    selectLogButton.Enabled = true;
                    return;
                }

                // Process the default CBS.log
                await ProcessCbsLog(@"C:\Windows\Logs\CBS\CBS.log");
            }
            catch (Exception ex)
            {
                string errorMessage = $"{DateTime.Now}: An error occurred in RunSfcButton_Click: {ex.Message}\nStack Trace: {ex.StackTrace}";
                File.AppendAllText(errorLogPath, errorMessage + "\n");
                statusLabel.Text = "An error occurred.";
                MessageBox.Show($"{errorMessage}\nError details saved to: {errorLogPath}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                runSfcButton.Enabled = true;
                selectLogButton.Enabled = true;
            }
        }

   

        private async Task<bool> RunSfcScannow()
        {
            try
            {
                ProcessStartInfo sfcProcessInfo = new ProcessStartInfo
                {
                    FileName = "sfc.exe",
                    Arguments = "/scannow",
                    UseShellExecute = true, // Ensures UAC prompt is visible
                    CreateNoWindow = true,
                    Verb = "runas" // Run as administrator
                };

                using (Process sfcProcess = new Process { StartInfo = sfcProcessInfo })
                {
                    sfcProcess.Start();

                    // Update status periodically
                    while (!sfcProcess.HasExited)
                    {
                        await Task.Delay(1000);
                        Invoke((Action)(() =>
                        {
                            statusLabel.Text = $"Running sfc /scannow... (Elapsed: {sfcProcess.TotalProcessorTime.TotalSeconds:F1}s)";
                            Application.DoEvents();
                        }));
                    }

                    if (sfcProcess.ExitCode != 0)
                    {
                        string errorMessage = $"{DateTime.Now}: sfc /scannow failed with exit code {sfcProcess.ExitCode}";
                        File.AppendAllText(errorLogPath, errorMessage + "\n");
                        Invoke((Action)(() =>
                        {
                            statusLabel.Text = "sfc /scannow failed to complete.";
                            MessageBox.Show($"{errorMessage}\nError details saved to: {errorLogPath}", "SFC Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }));
                        return false;
                    }

                    File.AppendAllText(errorLogPath, $"{DateTime.Now}: sfc /scannow completed successfully\n");
                    return true;
                }
            }
            catch (Exception ex)
            {
                string errorMessage = $"{DateTime.Now}: Error running sfc /scannow: {ex.Message}\nStack Trace: {ex.StackTrace}";
                File.AppendAllText(errorLogPath, errorMessage + "\n");
                Invoke((Action)(() =>
                {
                    statusLabel.Text = "Error running sfc /scannow.";
                    MessageBox.Show($"{errorMessage}\nError details saved to: {errorLogPath}", "SFC Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }));
                return false;
            }
        }

        private async Task ProcessCbsLog(string logPath)
        {
            try
            {
                statusLabel.Text = $"Processing {Path.GetFileName(logPath)}...";
                File.AppendAllText(errorLogPath, $"{DateTime.Now}: Processing {logPath}...\n");
                Application.DoEvents();

                string outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"SFC_Results_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
                string tempLogPath = Path.Combine(Path.GetTempPath(), $"CBS_temp_{DateTime.Now.Ticks}.log");
                string[] searchWords = { "error", "corrupt", "restored", "repaired" };
                Color[] highlightColors = { Color.Red, Color.Orange, Color.Green, Color.Yellow };

                // Try to access the log file with retries or copy to temp file
                string[] lines = null;
                bool fileAccessed = false;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try
                    {
                        if (File.Exists(logPath))
                        {
                            lines = File.ReadAllLines(logPath);
                            fileAccessed = true;
                            File.AppendAllText(errorLogPath, $"{DateTime.Now}: Successfully read {logPath} on attempt {attempt}\n");
                            break;
                        }
                        else
                        {
                            string errorMessage = $"{DateTime.Now}: Log file not found at: {logPath}";
                            File.AppendAllText(errorLogPath, errorMessage + "\n");
                            statusLabel.Text = "Log file not found.";
                            MessageBox.Show($"{errorMessage}\nError details saved to: {errorLogPath}", "File Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            return;
                        }
                    }
                    catch (IOException ex) when (ex.Message.Contains("being used by another process"))
                    {
                        File.AppendAllText(errorLogPath, $"{DateTime.Now}: {logPath} in use on attempt {attempt}: {ex.Message}\n");
                        statusLabel.Text = $"{Path.GetFileName(logPath)} in use, retrying ({attempt}/3)...";
                        await Task.Delay(2000);
                    }
                }

                // If direct access fails, try copying to a temp file
                if (!fileAccessed)
                {
                    try
                    {
                        File.Copy(logPath, tempLogPath, true);
                        lines = File.ReadAllLines(tempLogPath);
                        File.AppendAllText(errorLogPath, $"{DateTime.Now}: Successfully read {logPath} from temp copy at {tempLogPath}\n");
                        fileAccessed = true;
                    }
                    catch (Exception ex)
                    {
                        string errorMessage = $"{DateTime.Now}: Failed to access or copy {logPath}: {ex.Message}\nStack Trace: {ex.StackTrace}";
                        File.AppendAllText(errorLogPath, errorMessage + "\n");
                        statusLabel.Text = "Failed to access log file.";
                        MessageBox.Show($"{errorMessage}\nError details saved to: {errorLogPath}", "File Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                    finally
                    {
                        // Clean up temp file
                        if (File.Exists(tempLogPath))
                        {
                            try
                            {
                                File.Delete(tempLogPath);
                                File.AppendAllText(errorLogPath, $"{DateTime.Now}: Cleaned up temp file {tempLogPath}\n");
                            }
                            catch (Exception ex)
                            {
                                File.AppendAllText(errorLogPath, $"{DateTime.Now}: Failed to delete temp file {tempLogPath}: {ex.Message}\n");
                            }
                        }
                    }
                }

                if (!fileAccessed)
                {
                    string errorMessage = $"{DateTime.Now}: Unable to access {logPath} after retries and copy attempt.";
                    File.AppendAllText(errorLogPath, errorMessage + "\n");
                    statusLabel.Text = "Unable to access log file.";
                    MessageBox.Show($"{errorMessage}\nError details saved to: {errorLogPath}", "File Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // Process the log lines
                var filteredLines = lines
                    .Where(line => searchWords.Any(word => line.ToLower().Contains(word.ToLower())))
                    .ToList();

                if (filteredLines.Count == 0)
                {
                    statusLabel.Text = "No matching entries found.";
                    File.AppendAllText(errorLogPath, $"{DateTime.Now}: No lines containing 'error', 'corrupt', 'restored', or 'repaired' found in {logPath}\n");
                    MessageBox.Show($"No lines containing 'error', 'corrupt', 'restored', or 'repaired' found in {logPath}", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                // Display filtered lines in RichTextBox with highlighted words
                richTextBox1.Clear();
                foreach (string line in filteredLines)
                {
                    richTextBox1.AppendText(line + Environment.NewLine);
                    int lineStart = richTextBox1.TextLength - line.Length - Environment.NewLine.Length;

                    // Highlight each search word
                    for (int i = 0; i < searchWords.Length; i++)
                    {
                        string word = searchWords[i];
                        Color color = highlightColors[i];
                        int index = 0;
                        string lowerLine = line.ToLower();
                        string lowerWord = word.ToLower();

                        while ((index = lowerLine.IndexOf(lowerWord, index)) != -1)
                        {
                            richTextBox1.Select(lineStart + index, word.Length);
                            richTextBox1.SelectionBackColor = color;
                            index += word.Length;
                        }
                    }
                }

                // Copy filtered lines to clipboard
                string clipboardText = string.Join(Environment.NewLine, filteredLines);
                Clipboard.SetText(clipboardText);

                // Save filtered lines to text file
               // File.WriteAllLines(outputPath, filteredLines);

                // Open the text file
               // Process.Start("notepad.exe", outputPath);

                statusLabel.Text = "Complete. Results saved and copied to clipboard.";
               // File.AppendAllText(errorLogPath, $"{DateTime.Now}: Completed successfully. Results saved to: {outputPath}\n");
                //MessageBox.Show($"Results saved to: {outputPath}\nMatching lines copied to clipboard.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                string errorMessage = $"{DateTime.Now}: An error occurred in ProcessCbsLog: {ex.Message}\nStack Trace: {ex.StackTrace}";
                File.AppendAllText(errorLogPath, errorMessage + "\n");
                statusLabel.Text = "An error occurred.";
                MessageBox.Show($"{errorMessage}\nError details saved to: {errorLogPath}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async void selectLogButton_Click_1(object sender, EventArgs e)
        {
            try
            {
                statusLabel.Text = "Selecting CBS.log file...";
                runSfcButton.Enabled = false;
                selectLogButton.Enabled = false;
                richTextBox1.Clear();
                File.AppendAllText(errorLogPath, $"{DateTime.Now}: Opening file dialog to select CBS.log...\n");
                Application.DoEvents();

                using (OpenFileDialog openFileDialog = new OpenFileDialog
                {
                    Filter = "Log Files (*.log)|*.log|All Files (*.*)|*.*",
                    Title = "Select CBS.log File",
                    InitialDirectory = @"C:\Windows\Logs\CBS"
                })
                {
                    if (openFileDialog.ShowDialog() == DialogResult.OK)
                    {
                        await ProcessCbsLog(openFileDialog.FileName);
                    }
                    else
                    {
                        statusLabel.Text = "File selection canceled.";
                        File.AppendAllText(errorLogPath, $"{DateTime.Now}: File selection canceled by user\n");
                    }
                }
            }
            catch (Exception ex)
            {
                string errorMessage = $"{DateTime.Now}: An error occurred in SelectLogButton_Click: {ex.Message}\nStack Trace: {ex.StackTrace}";
                File.AppendAllText(errorLogPath, errorMessage + "\n");
                statusLabel.Text = "An error occurred.";
                MessageBox.Show($"{errorMessage}\nError details saved to: {errorLogPath}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                runSfcButton.Enabled = true;
                selectLogButton.Enabled = true;
            }
        }
    }

}

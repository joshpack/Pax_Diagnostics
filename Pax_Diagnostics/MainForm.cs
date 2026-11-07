using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Management;
namespace Pax_Diagnostics
{
    public partial class MainForm : Form
    {
        public MainForm()
        {
            GetOSInformation();
            GetCPUInformation();
            GetMemoryInformation();
            GetDiskInformation();
            GetNetworkInformation();
            GetMotherboardInfo();
            GetBIOSInfo();
            InitializeComponent();
        }

        private void sfcscannowToolStripMenuItem_Click(object sender, EventArgs e)
        {
            // Create and show the SFC Scanner form
            Forms.sfc_scanner sfcScannerForm = new Forms.sfc_scanner();
            sfcScannerForm.ShowDialog();
        }

        private void windowsReliabilityCenterToolStripMenuItem_Click(object sender, EventArgs e)
        {
            try
            {
                
                //File.AppendAllText(errorLogPath, $"{DateTime.Now}: Starting perfmon /rel...\n");
                Application.DoEvents();

                ProcessStartInfo perfmonProcessInfo = new ProcessStartInfo
                {
                    FileName = "perfmon.exe",
                    Arguments = "/rel",
                    UseShellExecute = true, // Ensures UAC prompt is visible
                    CreateNoWindow = true,
                    Verb = "runas" // Run as administrator
                };

                using (Process perfmonProcess = Process.Start(perfmonProcessInfo))
                {
                    // Since perfmon /rel opens a GUI, we don't wait for exit, just confirm it started
                  //  File.AppendAllText(errorLogPath, $"{DateTime.Now}: perfmon /rel started successfully\n");
                  //  statusLabel.Text = "Reliability Monitor launched.";
                  //  MessageBox.Show("Reliability Monitor has been launched.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                string errorMessage = $"{DateTime.Now}: Error running perfmon /rel: {ex.Message}\nStack Trace: {ex.StackTrace}";
                //File.AppendAllText(errorLogPath, errorMessage + "\n");
                //statusLabel.Text = "Error running Reliability Monitor.";
                MessageBox.Show($"{errorMessage}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
           
        }

        public void GetOSInformation()
        {
            OperatingSystem os = Environment.OSVersion;
            Console.WriteLine("\nOperating System:");
            Console.WriteLine($"  OS Name: {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
            Console.WriteLine($"  Version: {os.Version}");
            Console.WriteLine($"  Platform: {os.Platform}");
            Console.WriteLine($"  Architecture: {RuntimeInformation.OSArchitecture}");
            Console.WriteLine($"  Machine Name: {Environment.MachineName}");
            osLabel.Text = $"OS: {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({os.Version})";
        }

        void GetCPUInformation()
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_Processor"))
                
            {
                Console.WriteLine("\nProcessor:");
                foreach (ManagementObject obj in searcher.Get())
                {
                    Console.WriteLine($"  Name: {obj["Name"]}");
                    Console.WriteLine($"  Manufacturer: {obj["Manufacturer"]}");
                    Console.WriteLine($"  Number of Cores: {obj["NumberOfCores"]}");
                    Console.WriteLine($"  Number of Threads: {obj["NumberOfLogicalProcessors"]}");
                    Console.WriteLine($"  Current Clock Speed: {obj["CurrentClockSpeed"]} MHz");
                    Console.WriteLine($"  Max Clock Speed: {obj["MaxClockSpeed"]} MHz");
                }
            }
        }

        static void GetMemoryInformation()
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem"))
            {
                Console.WriteLine("\nMemory:");
                foreach (ManagementObject obj in searcher.Get())
                {
                    Console.WriteLine($"  Total Physical Memory: {ConvertBytesToGB((ulong)obj["TotalVisibleMemorySize"])} GB");
                    Console.WriteLine($"  Free Physical Memory: {ConvertBytesToGB((ulong)obj["FreePhysicalMemory"])} GB");
                }
            }
        }

        static void GetDiskInformation()
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive"))
            {
                Console.WriteLine("\nDisk Drives:");
                foreach (ManagementObject obj in searcher.Get())
                {
                    Console.WriteLine($"  Model: {obj["Model"]}");
                    Console.WriteLine($"  Size: {ConvertBytesToGB((ulong)obj["Size"])} GB");
                    Console.WriteLine($"  Interface: {obj["InterfaceType"]}");
                }
            }
        }

        static void GetNetworkInformation()
        {
            Console.WriteLine("\nNetwork:");
            Console.WriteLine($"  Host Name: {Dns.GetHostName()}");

            foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList)
            {
                Console.WriteLine($"  IP Address: {ip}");
            }

            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_NetworkAdapter WHERE PhysicalAdapter=True"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    Console.WriteLine($"  Adapter: {obj["Name"]}");
                    Console.WriteLine($"  MAC Address: {obj["MACAddress"]}");
                }
            }
        }

        static void GetMotherboardInfo()
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_BaseBoard"))
            {
                Console.WriteLine("\nMotherboard:");
                foreach (ManagementObject obj in searcher.Get())
                {
                    Console.WriteLine($"  Manufacturer: {obj["Manufacturer"]}");
                    Console.WriteLine($"  Product: {obj["Product"]}");
                    Console.WriteLine($"  Serial Number: {obj["SerialNumber"]}");
                }
            }
        }

        static void GetBIOSInfo()
        {
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT * FROM Win32_BIOS"))
            {
                Console.WriteLine("\nBIOS:");
                foreach (ManagementObject obj in searcher.Get())
                {
                    Console.WriteLine($"  Manufacturer: {obj["Manufacturer"]}");
                    Console.WriteLine($"  Version: {obj["SMBIOSBIOSVersion"]}");
                    Console.WriteLine($"  Release Date: {obj["ReleaseDate"]}");
                }
            }
        }

        static double ConvertBytesToGB(ulong bytes)
        {
            return Math.Round(bytes / 1024.0 / 1024.0 / 1024.0, 2);
        }



    }
}

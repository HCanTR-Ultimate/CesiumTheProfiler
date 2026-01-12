using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;

namespace CesiumTheProfiler
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<CpuCoreViewModel> CpuCores { get; } = new();
        public ObservableCollection<CpuProcessViewModel> CpuProcesses { get; } = new();
        public ObservableCollection<RamProcessViewModel> RamProcesses { get; } = new();

        DispatcherTimer _timer;

        readonly Dictionary<int, TimeSpan> _lastCpuTimes = new();
        DateTime _lastSampleTime = DateTime.UtcNow;
        readonly int _logicalCoreCount = Environment.ProcessorCount;
        private double _totalSystemRamMB = 0;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            LoadCacheInfo();
            InitializeCpuCores();
            ApplyCoreSorting();
            GetRamInfo();
            InitializeTotalRam();
            StartTimer();
        }

        void LoadCacheInfo()
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Level, InstalledSize FROM Win32_CacheMemory");

            foreach (ManagementObject obj in searcher.Get())
            {
                if (obj["Level"] == null || obj["InstalledSize"] == null)
                    continue;

                int level = Convert.ToInt32(obj["Level"]);
                double sizeKB = Convert.ToDouble(obj["InstalledSize"]);

                string text = sizeKB >= 1024
                    ? $"{sizeKB / 1024:N1} MB"
                    : $"{sizeKB} KB";

                if (level == 3) CpuL1.Text = text;
                else if (level == 4) CpuL2.Text = text;
                else if (level == 5) CpuL3.Text = text;
            }
        }

        void InitializeCpuCores()
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PerfFormattedData_PerfOS_Processor");

            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name) || name == "_Total")
                    continue;

                CpuCores.Add(new CpuCoreViewModel
                {
                    CoreKey = name,
                    CoreName = $"Çekirdek {name}"
                });
            }
        }

        void ApplyCoreSorting()
        {
            var view = CollectionViewSource.GetDefaultView(CpuCores);
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(
                new SortDescription(nameof(CpuCoreViewModel.UsageValue),
                                    ListSortDirection.Descending));
        }

        void StartTimer()
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };

            _timer.Tick += (_, _) => _ = UpdateAsync();
            _timer.Start();
        }

        async Task UpdateAsync()
        {
            var generalCpu = await Task.Run(GetGeneralCpuUsage);
            var coreLoads = await Task.Run(GetCoreLoads);
            var topCpuProcesses = await Task.Run(GetTopProcesses);

            await GetMemoryUsage();

            var topMemoryProcesses = await Task.Run(GetTopRamProcesses);
            var uptime = GetSystemUptime();

            Dispatcher.Invoke(() =>
            {
                UpdateGeneralCpuUI(generalCpu);

                foreach (var core in CpuCores)
                {
                    if (coreLoads.TryGetValue(core.CoreKey, out int load))
                        core.UsageValue = load;
                }

                CpuProcesses.Clear();
                foreach (var p in topCpuProcesses) CpuProcesses.Add(p);

                RamProcesses.Clear();
                foreach (var p in topMemoryProcesses) RamProcesses.Add(p);

                UptimeText.Text = $"{uptime.Days} gün {uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}";
            });
        }


        int GetGeneralCpuUsage()
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT LoadPercentage FROM Win32_Processor");

            foreach (ManagementObject obj in searcher.Get())
                return Convert.ToInt32(obj["LoadPercentage"]);

            return 0;
        }
        async Task GetMemoryUsage()
        {

            try
            {

                using var osSearcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem");
                double totalGB = 0, freeGB = 0, usedPercent = 0, usedGB = 0;

                foreach (ManagementObject obj in osSearcher.Get())
                {
                    ulong totalKB = Convert.ToUInt64(obj["TotalVisibleMemorySize"]);
                    ulong freeKB = Convert.ToUInt64(obj["FreePhysicalMemory"]);

                    totalGB = Math.Round(totalKB / 1024.0 / 1024.0, 2);
                    freeGB = Math.Round(freeKB / 1024.0 / 1024.0, 2);
                    usedGB = Math.Round(totalGB - freeGB, 2);
                    usedPercent = Math.Round((usedGB / totalGB) * 100, 2);
                }

                using var perfSearcher = new ManagementObjectSearcher("SELECT * FROM Win32_PerfFormattedData_PerfOS_Memory");
                string commitText = "0 / 0 GB";
                double cacheGB = 0;
                double compressedMB = 0;

                foreach (ManagementObject perfObj in perfSearcher.Get())
                {
                    ulong commBytes = GetSafeUint64(perfObj, "CommittedBytes");
                    ulong limitBytes = GetSafeUint64(perfObj, "CommitLimit");
                    commitText = $"{Math.Round(commBytes / 1024.0 / 1024.0 / 1024.0, 2)} / {Math.Round(limitBytes / 1024.0 / 1024.0 / 1024.0, 2)} GB";

                    ulong cacheBytes = GetSafeUint64(perfObj, "CacheBytes");
                    cacheGB = Math.Round(cacheBytes / 1024.0 / 1024.0 / 1024.0, 2);

                    ulong compBytes = GetSafeUint64(perfObj, "CompressedBytes");
                    compressedMB = Math.Round(compBytes / 1024.0 / 1024.0, 2);
                }

                App.Current.Dispatcher.Invoke(() =>
                {
                    RamUsedPercentage.Text = $"{usedPercent}%";
                    RamUsed.Text = $"{usedGB} GB";
                    RamFree.Text = $"{freeGB} GB";
                    RamUsedProgressbar.Value = usedPercent;
                    if (usedPercent > 90)
                        RamUsedProgressbar.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE53935"));
                    RamCommit.Text = commitText;
                    RamCache.Text = $"{cacheGB} GB";
                    RamCompressed.Text = $"{compressedMB} MB";
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Hata: " + ex.Message);
            }
        }

        private ulong GetSafeUint64(ManagementObject obj, string propertyName)
        {
            try
            {
                return obj[propertyName] != null ? Convert.ToUInt64(obj[propertyName]) : 0;
            }
            catch
            {
                return 0;
            }
        }


        Dictionary<string, int> GetCoreLoads()
        {
            var dict = new Dictionary<string, int>();

            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor");

            foreach (ManagementObject obj in searcher.Get())
            {
                string name = obj["Name"]?.ToString();
                if (string.IsNullOrWhiteSpace(name) || name == "_Total")
                    continue;

                dict[name] = Convert.ToInt32(obj["PercentProcessorTime"]);
            }

            return dict;
        }

        List<CpuProcessViewModel> GetTopProcesses()
        {
            var now = DateTime.UtcNow;
            var elapsedMs = (now - _lastSampleTime).TotalMilliseconds;
            _lastSampleTime = now;

            var list = new List<CpuProcessViewModel>();

            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var totalCpu = p.TotalProcessorTime;

                    if (!_lastCpuTimes.TryGetValue(p.Id, out var last))
                    {
                        _lastCpuTimes[p.Id] = totalCpu;
                        continue;
                    }

                    var deltaMs = (totalCpu - last).TotalMilliseconds;
                    _lastCpuTimes[p.Id] = totalCpu;

                    int cpu = (int)((deltaMs / elapsedMs) * 100 / _logicalCoreCount);
                    if (cpu <= 0)
                        continue;

                    list.Add(new CpuProcessViewModel
                    {
                        Name = p.ProcessName,
                        Pid = p.Id,
                        ProcessUsage = cpu
                    });
                }
                catch { }
            }

            return list
                .OrderByDescending(x => x.ProcessUsage)
                .Take(5)
                .ToList();


        }
        List<RamProcessViewModel> GetTopRamProcesses()
        {
            var list = new List<RamProcessViewModel>();
            var allProcesses = Process.GetProcesses();

            foreach (var p in allProcesses)
            {
                try
                {
                    // Tüm verileri process açıkken oku
                    string processName = p.ProcessName;
                    int pid = p.Id;
                    double ramBytes = p.PrivateMemorySize64;
                    double ramMB = ramBytes / 1024.0 / 1024.0;
                    double ramPercent = (ramMB / _totalSystemRamMB) * 100;

                    if (ramMB < 0.1) continue;

                    // Verileri al ve listeye ekle
                    list.Add(new RamProcessViewModel
                    {
                        Name = processName,
                        Pid = pid,
                        ProcessUsageMB = ramMB,
                        ProcessUsagePercentage = ramPercent
                    });
                }
                catch
                {
                    continue;
                }
                finally
                {
                    p.Dispose();
                }
            }

            return list
                .OrderByDescending(x => x.ProcessUsageMB)
                .Take(10)
                .ToList();
        }

        void UpdateGeneralCpuUI(int load)
        {
            GeneralCpuProgress.Value = load;
            GeneralCpuText.Text = $"{load}%";

            GeneralCpuProgress.Foreground =
                load > 80
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE53935"))
                    : load > 60
                        ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF1C40F"))
                        : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3498DB"));
        }
        TimeSpan GetSystemUptime()
        {
            return TimeSpan.FromMilliseconds(Environment.TickCount64);
        }

        void GetRamInfo()
        {
            using var ramSearcher = new ManagementObjectSearcher(
                "SELECT Capacity, Speed, FormFactor, MemoryType, Manufacturer, PartNumber FROM Win32_PhysicalMemory");

            foreach (ManagementObject ram in ramSearcher.Get())
            {
                ulong capacityBytes = Convert.ToUInt64(ram["Capacity"]);
                double capacityGB = capacityBytes / 1024.0 / 1024 / 1024;

                int speed = ram["Speed"] != null ? Convert.ToInt32(ram["Speed"]) : 0;

                int formFactorCode = ram["FormFactor"] != null ? Convert.ToInt32(ram["FormFactor"]) : 0;
                string formFactor = formFactorCode switch
                {
                    0 => "Unknown",
                    8 => "DIMM",
                    12 => "SODIMM",
                    18 => "SO-DIMM",
                    _ => "Other"
                };

                int memoryType = ram["MemoryType"] != null ? Convert.ToInt32(ram["MemoryType"]) : 0;

                RamCapacity.Text = $"{capacityGB:N1} GB";
                RamSpeed.Text = $"{speed} MHz";
                RamFormFactor.Text = $"{formFactor}";
            }

            int totalSlots = 0;
            using var slotSearcher = new ManagementObjectSearcher(
                "SELECT MemoryDevices FROM Win32_PhysicalMemoryArray");
            foreach (ManagementObject array in slotSearcher.Get())
            {
                totalSlots += array["MemoryDevices"] != null ? Convert.ToInt32(array["MemoryDevices"]) : 0;
            }

            int installedModules = 0;
            using var installedSearcher = new ManagementObjectSearcher(
                "SELECT Capacity FROM Win32_PhysicalMemory");
            foreach (ManagementObject module in installedSearcher.Get())
            {
                if (module["Capacity"] != null)
                    installedModules++;
            }

            RamSlots.Text = $"{installedModules} / {totalSlots}";
        }

        private void InitializeTotalRam()
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                _totalSystemRamMB = Convert.ToUInt64(obj["TotalVisibleMemorySize"]) / 1024.0;
            }
        }

        

        
    }

    public sealed class CpuCoreViewModel : INotifyPropertyChanged
    {
        int _usage;
        Brush _brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3498DB"));

        public string CoreKey { get; set; } = "";
        public string CoreName { get; set; } = "";

        public int UsageValue
        {
            get => _usage;
            set
            {
                if (_usage == value) return;
                _usage = value;
                UpdateColor();
                OnPropertyChanged();
                OnPropertyChanged(nameof(UsageText));
            }
        }

        public Brush UsageBrush
        {
            get => _brush;
            private set
            {
                _brush = value;
                OnPropertyChanged();
            }
        }

        public string UsageText => $"{UsageValue}%";

        void UpdateColor()
        {
            UsageBrush =
                UsageValue > 80
                    ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFE53935"))
                    : UsageValue > 60
                        ? new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF1C40F"))
                        : new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF3498DB"));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public sealed class CpuProcessViewModel : INotifyPropertyChanged
    {
        int _usage;

        public string Name { get; set; } = "";
        public int Pid { get; set; }

        public string DisplayName => $"{Name} (PID:{Pid})";

        public int ProcessUsage
        {
            get => _usage;
            set
            {
                if (_usage == value) return;
                _usage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProcessUsageText));
            }
        }

        public string ProcessUsageText => $"{ProcessUsage}%";

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
    public sealed class RamProcessViewModel : INotifyPropertyChanged
    {
        private double _usageMB;
        private double _usagePercentage;
        public string Name { get; set; } = "";
        public int Pid { get; set; }
        public string DisplayName => $"{Name} (PID: {Pid})";

        public double ProcessUsageMB
        {
            get => _usageMB;
            set
            {
                if (Math.Abs(_usageMB - value) < 0.01) return;
                _usageMB = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProcessUsageMBDisplay));
            }
        }

        public double ProcessUsagePercentage
        {
            get => _usagePercentage;
            set
            {
                if (Math.Abs(_usagePercentage - value) < 0.01) return;
                _usagePercentage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProcessUsagePercentageDisplay));
            }
        }

        public string ProcessUsageMBDisplay => $"{ProcessUsageMB:N2} MB";
        public string ProcessUsagePercentageDisplay => $"{ProcessUsagePercentage:N1}%";

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WpfSerialTool
{
    public partial class MainWindow : Window
    {
        private SerialPort _serialPort;
        private DispatcherTimer _clockTimer;
        private DispatcherTimer _ledResetTimer;
        private DispatcherTimer _pollTimer;

        private readonly List<byte> _receiveBuffer = new List<byte>();
        private readonly object _bufferLock = new object();

        private readonly List<byte> _pollSlaveIds = new List<byte>();
        private int _pollIndex = 0;
        private bool _isPolling = false;

        // FA + slaveid + 03 + 10 + 16字节数据 + CRC2 = 22字节
        private const int FixedResponseLength = 22;

        public ObservableCollection<SlaveDataModel> SlaveDataList { get; set; }

        public MainWindow()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            InitializeComponent();

            SlaveDataList = new ObservableCollection<SlaveDataModel>();
            dgSlaveData.ItemsSource = SlaveDataList;

            InitSerialPort();
            LoadPorts();
            InitClock();
            InitLedTimer();
            InitPollTimer();
        }

        private void InitSerialPort()
        {
            _serialPort = new SerialPort
            {
                Parity = Parity.None,
                DataBits = 8,
                StopBits = StopBits.One,
                Encoding = Encoding.GetEncoding("GBK")
            };

            _serialPort.DataReceived += SerialPort_DataReceived;
        }

        private void InitClock()
        {
            _clockTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _clockTimer.Tick += (s, e) =>
            {
                txtClock.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            };
            _clockTimer.Start();
        }

        private void InitLedTimer()
        {
            _ledResetTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _ledResetTimer.Tick += (s, e) =>
            {
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    ledSend.Fill = Brushes.Gray;
                    ledReceive.Fill = Brushes.Gray;
                }
                _ledResetTimer.Stop();
            };
        }

        private void InitPollTimer()
        {
            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };

            _pollTimer.Tick += (s, e) =>
            {
                try
                {
                    if (!_isPolling || _pollSlaveIds.Count == 0)
                        return;

                    if (_serialPort == null || !_serialPort.IsOpen)
                    {
                        StopPollingInternal("串口未打开，轮询已停止。");
                        return;
                    }

                    if (_pollIndex >= _pollSlaveIds.Count)
                        _pollIndex = 0;

                    byte slaveId = _pollSlaveIds[_pollIndex];
                    _pollIndex++;

                    QuerySlave(slaveId, false);
                }
                catch (Exception ex)
                {
                    AppendLog("错误", "轮询异常：" + ex.Message);
                }
            };
        }

        private void LoadPorts()
        {
            cmbPorts.Items.Clear();
            var ports = SerialPort.GetPortNames().OrderBy(p => p).ToArray();

            foreach (var port in ports)
            {
                cmbPorts.Items.Add(port);
            }

            if (cmbPorts.Items.Count > 0)
            {
                cmbPorts.SelectedIndex = 0;
            }
        }

        private void btnRefreshPorts_Click(object sender, RoutedEventArgs e)
        {
            LoadPorts();
            AppendLog("系统", "串口列表已刷新。");
        }

        private void btnOpenClose_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!_serialPort.IsOpen)
                {
                    if (cmbPorts.SelectedItem == null)
                    {
                        MessageBox.Show("请选择串口号。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

                    _serialPort.PortName = cmbPorts.SelectedItem.ToString();

                    if (cmbBaudRate.SelectedItem is ComboBoxItem baudItem)
                    {
                        _serialPort.BaudRate = int.Parse(baudItem.Content.ToString());
                    }
                    else
                    {
                        _serialPort.BaudRate = 115200;
                    }

                    _serialPort.Open();

                    btnOpenClose.Content = "关闭串口";
                    btnStartCalibrate.IsEnabled = true;
                    btnStopCalibrate.IsEnabled = true;
                    btnQuerySlave.IsEnabled = true;
                    btnSlaveMinus.IsEnabled = true;
                    btnSlavePlus.IsEnabled = true;
                    btnStartPolling.IsEnabled = true;
                    btnStopPolling.IsEnabled = true;

                    ledPort.Fill = Brushes.LimeGreen;
                    txtPortStatus.Text = "已连接";
                    txtPortStatus.Foreground = Brushes.LimeGreen;

                    AppendLog("系统", $"串口 {_serialPort.PortName} 已打开，波特率 {_serialPort.BaudRate}。");
                }
                else
                {
                    StopPollingInternal("串口关闭，轮询已停止。");

                    _serialPort.Close();

                    lock (_bufferLock)
                    {
                        _receiveBuffer.Clear();
                    }

                    btnOpenClose.Content = "打开串口";
                    btnStartCalibrate.IsEnabled = false;
                    btnStopCalibrate.IsEnabled = false;
                    btnQuerySlave.IsEnabled = false;
                    btnSlaveMinus.IsEnabled = false;
                    btnSlavePlus.IsEnabled = false;
                    btnStartPolling.IsEnabled = false;
                    btnStopPolling.IsEnabled = false;

                    ledPort.Fill = Brushes.Gray;
                    ledCalibrate.Fill = Brushes.Gray;
                    ledSend.Fill = Brushes.Gray;
                    ledReceive.Fill = Brushes.Gray;

                    txtPortStatus.Text = "未连接";
                    txtPortStatus.Foreground = Brushes.Gray;
                    txtCalibrateStatus.Text = "待机";
                    txtCalibrateStatus.Foreground = Brushes.Gray;

                    AppendLog("系统", "串口已关闭。");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("串口操作失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                AppendLog("错误", ex.Message);
            }
        }

        private void btnSlaveMinus_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSlaveId(out byte slaveId))
                return;

            int newId = slaveId - 1;
            if (newId < 1) newId = 1;
            txtSlaveId.Text = newId.ToString();
        }

        private void btnSlavePlus_Click(object sender, RoutedEventArgs e)
        {
            if (!TryGetSlaveId(out byte slaveId))
                return;

            int newId = slaveId + 1;
            if (newId > 247) newId = 247;
            txtSlaveId.Text = newId.ToString();
        }

        private void btnQuerySlave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!TryGetSlaveId(out byte slaveId))
                    return;

                QuerySlave(slaveId, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("问询失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                AppendLog("错误", "问询失败：" + ex.Message);
            }
        }

        private void btnStartPolling_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                {
                    MessageBox.Show("请先打开串口。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                List<byte> ids = ParseSlaveIdList(txtSlaveList.Text);
                if (ids.Count == 0)
                {
                    MessageBox.Show("请输入有效的从站列表，例如：1,2,3,5", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!int.TryParse(txtPollInterval.Text.Trim(), out int intervalMs))
                {
                    MessageBox.Show("轮询间隔请输入数字。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (intervalMs < 50)
                {
                    MessageBox.Show("轮询间隔建议不小于 50ms。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                _pollSlaveIds.Clear();
                _pollSlaveIds.AddRange(ids);
                _pollIndex = 0;
                _pollTimer.Interval = TimeSpan.FromMilliseconds(intervalMs);
                _isPolling = true;
                _pollTimer.Start();

                AppendLog("系统", $"开始轮询从站：{string.Join(",", _pollSlaveIds)}，间隔={intervalMs}ms");
            }
            catch (Exception ex)
            {
                AppendLog("错误", "启动轮询失败：" + ex.Message);
            }
        }

        private void btnStopPolling_Click(object sender, RoutedEventArgs e)
        {
            StopPollingInternal("用户已停止轮询。");
        }

        private void StopPollingInternal(string message)
        {
            _isPolling = false;
            _pollTimer.Stop();
            _pollSlaveIds.Clear();
            _pollIndex = 0;

            if (!string.IsNullOrWhiteSpace(message))
            {
                AppendLog("系统", message);
            }
        }

        private List<byte> ParseSlaveIdList(string input)
        {
            var result = new List<byte>();

            if (string.IsNullOrWhiteSpace(input))
                return result;

            string[] parts = input.Split(new[] { ',', '，', ';', '；', ' ' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                if (byte.TryParse(part.Trim(), out byte id))
                {
                    if (id >= 1 && id <= 247 && !result.Contains(id))
                    {
                        result.Add(id);
                    }
                }
            }

            result.Sort();
            return result;
        }

        private void btnStartCalibrate_Click(object sender, RoutedEventArgs e)
        {
            SendHexCommand("FF AA FF", "开始校准");
            ledCalibrate.Fill = Brushes.DeepSkyBlue;
            txtCalibrateStatus.Text = "校准中";
            txtCalibrateStatus.Foreground = Brushes.DeepSkyBlue;
        }

        private void btnStopCalibrate_Click(object sender, RoutedEventArgs e)
        {
            SendHexCommand("AA FF AA", "停止校准");
            ledCalibrate.Fill = Brushes.OrangeRed;
            txtCalibrateStatus.Text = "已停止";
            txtCalibrateStatus.Foreground = Brushes.OrangeRed;
        }

        private void btnClearLog_Click(object sender, RoutedEventArgs e)
        {
            txtReceiveArea.Clear();
        }

        private bool TryGetSlaveId(out byte slaveId)
        {
            slaveId = 0;

            if (!byte.TryParse(txtSlaveId.Text.Trim(), out slaveId))
            {
                MessageBox.Show("从站地址请输入 1~247 的十进制数字。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (slaveId < 1 || slaveId > 247)
            {
                MessageBox.Show("从站地址范围应为 1~247。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }

        private void QuerySlave(byte slaveId, bool writeLog)
        {
            if (_serialPort == null || !_serialPort.IsOpen)
            {
                MessageBox.Show("请先打开串口。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            byte[] frame = BuildQueryFrame(slaveId);
            _serialPort.Write(frame, 0, frame.Length);

            ledSend.Fill = Brushes.Cyan;
            _ledResetTimer.Stop();
            _ledResetTimer.Start();

            if (writeLog)
            {
                AppendLog("发送", $"问询从站[{slaveId}] -> {BitConverter.ToString(frame).Replace("-", " ")}");
            }
        }

        private byte[] BuildQueryFrame(byte slaveId)
        {
            byte[] cmd = new byte[8];
            cmd[0] = slaveId;
            cmd[1] = 0x03;
            cmd[2] = 0x0B;
            cmd[3] = 0xB8;
            cmd[4] = 0x00;
            cmd[5] = 0x08;

            ushort crc = ComputeModbusCrc(cmd, 0, 6);
            cmd[6] = (byte)(crc & 0xFF);
            cmd[7] = (byte)((crc >> 8) & 0xFF);

            return cmd;
        }

        private void SendHexCommand(string hex, string description)
        {
            try
            {
                if (_serialPort == null || !_serialPort.IsOpen)
                {
                    MessageBox.Show("请先打开串口。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                byte[] data = HexStringToBytes(hex);
                _serialPort.Write(data, 0, data.Length);

                ledSend.Fill = Brushes.Cyan;
                _ledResetTimer.Stop();
                _ledResetTimer.Start();

                AppendLog("发送", $"{description} -> {hex}");
            }
            catch (Exception ex)
            {
                MessageBox.Show("发送失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                AppendLog("错误", "发送失败：" + ex.Message);
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                int len = _serialPort.BytesToRead;
                if (len <= 0)
                    return;

                byte[] buffer = new byte[len];
                _serialPort.Read(buffer, 0, len);

                lock (_bufferLock)
                {
                    _receiveBuffer.AddRange(buffer);
                    TryParseReceivedData();
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    AppendLog("错误", "接收失败：" + ex.Message);
                });
            }
        }

        private void TryParseReceivedData()
        {
            while (true)
            {
                if (_receiveBuffer.Count == 0)
                    return;

                // 协议帧：0xFA开头
                if (_receiveBuffer[0] == 0xFA)
                {
                    if (_receiveBuffer.Count < FixedResponseLength)
                        return;

                    byte[] frame = _receiveBuffer.Take(FixedResponseLength).ToArray();

                    if (IsValidResponseFrame(frame, out string error))
                    {
                        _receiveBuffer.RemoveRange(0, FixedResponseLength);

                        Dispatcher.Invoke(() =>
                        {
                            ledReceive.Fill = Brushes.LawnGreen;
                            _ledResetTimer.Stop();
                            _ledResetTimer.Start();

                            string rawHex = BitConverter.ToString(frame).Replace("-", " ");
                            AppendLog("接收", rawHex);
                            ParseAndShowSlaveData(frame, rawHex);
                        });

                        continue;
                    }
                    else
                    {
                        // 如果是0xFA开头但不是有效22字节帧，尝试当成文本
                        string text = TryDecodeBufferAsText(_receiveBuffer.ToArray());
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            _receiveBuffer.Clear();

                            Dispatcher.Invoke(() =>
                            {
                                ledReceive.Fill = Brushes.LawnGreen;
                                _ledResetTimer.Stop();
                                _ledResetTimer.Start();

                                AppendLog("接收文本", text);
                            });

                            return;
                        }

                        byte bad = _receiveBuffer[0];
                        _receiveBuffer.RemoveAt(0);

                        Dispatcher.Invoke(() =>
                        {
                            AppendLog("错误", $"收到疑似无效帧，已丢弃字节 0x{bad:X2}，原因：{error}");
                        });

                        continue;
                    }
                }

                // 非0xFA开头，当文本处理
                byte[] textBytes = _receiveBuffer.ToArray();
                _receiveBuffer.Clear();

                string receivedText = TryDecodeBufferAsText(textBytes);
                string hexText = BitConverter.ToString(textBytes).Replace("-", " ");

                Dispatcher.Invoke(() =>
                {
                    ledReceive.Fill = Brushes.LawnGreen;
                    _ledResetTimer.Stop();
                    _ledResetTimer.Start();

                    if (!string.IsNullOrWhiteSpace(receivedText))
                    {
                        AppendLog("接收文本", receivedText);
                    }
                    else
                    {
                        AppendLog("接收", hexText);
                    }
                });

                return;
            }
        }

        private string TryDecodeBufferAsText(byte[] data)
        {
            if (data == null || data.Length == 0)
                return string.Empty;

            try
            {
                string text = _serialPort.Encoding.GetString(data).Trim('\0', '\r', '\n', ' ');
                return text;
            }
            catch
            {
                return string.Empty;
            }
        }

        private bool IsValidResponseFrame(byte[] frame, out string error)
        {
            error = string.Empty;

            if (frame == null || frame.Length != FixedResponseLength)
            {
                error = "帧长度不正确。";
                return false;
            }

            if (frame[0] != 0xFA)
            {
                error = "帧头不是 0xFA。";
                return false;
            }

            if (frame[2] != 0x03)
            {
                error = $"功能码错误，实际为 0x{frame[2]:X2}";
                return false;
            }

            if (frame[3] != 0x10)
            {
                error = $"数据长度标记错误，实际为 0x{frame[3]:X2}";
                return false;
            }

            ushort calcCrc = ComputeModbusCrc(frame, 0, frame.Length - 2);
            ushort recvCrc = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));

            if (calcCrc != recvCrc)
            {
                error = $"CRC错误，计算值=0x{calcCrc:X4}，接收值=0x{recvCrc:X4}";
                return false;
            }

            return true;
        }

        private ushort ComputeModbusCrc(byte[] data, int start, int length)
        {
            ushort crc = 0xFFFF;

            for (int i = start; i < start + length; i++)
            {
                crc ^= data[i];

                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc >>= 1;
                        crc ^= 0xA001;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }

            return crc;
        }

        private short ReadInt16BigEndian(byte high, byte low)
        {
            return (short)((high << 8) | low);
        }

        private ushort ReadUInt16BigEndian(byte high, byte low)
        {
            return (ushort)((high << 8) | low);
        }

        private double CombineIntegerFraction(short intPart, ushort fracPart)
        {
            // 先取绝对值，处理负数
            double absValue = Math.Abs(intPart);
            double fraction = fracPart;

            int decimalPlaces = 0; // 小数位数

            // 判断小数位数，根据fracPart的大小来决定
            if (fracPart < 10)
            {
                decimalPlaces = 1;  // 1 位小数
                fraction /= 10;     // 除以 10
            }
            else if (fracPart < 100)
            {
                decimalPlaces = 2;  // 2 位小数
                fraction /= 100;    // 除以 100
            }
            else if (fracPart < 1000)
            {
                decimalPlaces = 3;  // 3 位小数
                fraction /= 1000;   // 除以 1000
            }
            else if (fracPart < 10000)
            {
                decimalPlaces = 4;  // 4 位小数
                fraction /= 10000;  // 除以 10000
            }
            else if (fracPart < 100000)
            {
                decimalPlaces = 5;  // 5 位小数
                fraction /= 100000;  // 除以 100000
            }
            else
            {
                // 如果fracPart大于9999，视为无效，抛出异常
                throw new ArgumentException("Invalid fraction part: exceeds maximum value.");
            }

            // 计算最终值
            double result = absValue + fraction;

            // 如果原始的intPart是负数，则返回负值
            if (intPart < 0)
            {
                result = -result;
            }

            // 使用Math.Round进行四舍五入，确保精度正确
            return Math.Round(result, decimalPlaces);
        }

        private void ParseAndShowSlaveData(byte[] frame, string rawHex)
        {
            try
            {
                byte slaveId = frame[1];

                short rollInt = ReadInt16BigEndian(frame[4], frame[5]);
                ushort rollFrac = ReadUInt16BigEndian(frame[6], frame[7]);

                short pitchInt = ReadInt16BigEndian(frame[8], frame[9]);
                ushort pitchFrac = ReadUInt16BigEndian(frame[10], frame[11]);

                short yawInt = ReadInt16BigEndian(frame[12], frame[13]);
                ushort yawFrac = ReadUInt16BigEndian(frame[14], frame[15]);

                short tempInt = ReadInt16BigEndian(frame[16], frame[17]);
                ushort tempFrac = ReadUInt16BigEndian(frame[18], frame[19]);

                double roll = CombineIntegerFraction(rollInt, rollFrac);
                double pitch = CombineIntegerFraction(pitchInt, pitchFrac);
                double yaw = CombineIntegerFraction(yawInt, yawFrac);
                double temperature = CombineIntegerFraction(tempInt, tempFrac)-100.0;

                AppendLog("解析",
                    $"从站[{slaveId}] -> 横滚角={roll:F4}°, 俯仰角={pitch:F4}°, 航向角={yaw:F4}°, 温度={temperature:F4}℃");

                UpdateSlaveDataGrid(slaveId, roll, pitch, yaw, temperature, rawHex);
            }
            catch (Exception ex)
            {
                AppendLog("错误", "数据解析失败：" + ex.Message);
            }
        }

        private void UpdateSlaveDataGrid(byte slaveId, double roll, double pitch, double yaw, double temperature, string rawHex)
        {
            var item = SlaveDataList.FirstOrDefault(x => x.SlaveId == slaveId);
            string now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            if (item == null)
            {
                SlaveDataList.Add(new SlaveDataModel
                {
                    SlaveId = slaveId,
                    Roll = roll,
                    Pitch = pitch,
                    Yaw = yaw,
                    Temperature = temperature,
                    UpdateTime = now,
                    RawHex = rawHex
                });

                SortSlaveDataGrid();
            }
            else
            {
                item.Roll = roll;
                item.Pitch = pitch;
                item.Yaw = yaw;
                item.Temperature = temperature;
                item.UpdateTime = now;
                item.RawHex = rawHex;
            }
        }

        private void SortSlaveDataGrid()
        {
            var sorted = SlaveDataList.OrderBy(x => x.SlaveId).ToList();
            SlaveDataList.Clear();
            foreach (var item in sorted)
            {
                SlaveDataList.Add(item);
            }
        }

        private void AppendLog(string type, string message)
        {
            txtReceiveArea.AppendText($"[{DateTime.Now:HH:mm:ss}] [{type}] {message}{Environment.NewLine}");
            txtReceiveArea.ScrollToEnd();
        }

        private byte[] HexStringToBytes(string hex)
        {
            hex = hex.Replace(" ", "");

            if (hex.Length % 2 != 0)
            {
                throw new Exception("HEX字符串长度不正确。");
            }

            byte[] bytes = new byte[hex.Length / 2];

            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }

            return bytes;
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                _clockTimer?.Stop();
                _ledResetTimer?.Stop();
                _pollTimer?.Stop();

                if (_serialPort != null)
                {
                    if (_serialPort.IsOpen)
                        _serialPort.Close();

                    _serialPort.DataReceived -= SerialPort_DataReceived;
                    _serialPort.Dispose();
                }
            }
            catch
            {
            }

            base.OnClosed(e);
        }
    }

    public class SlaveDataModel : INotifyPropertyChanged
    {
        private byte _slaveId;
        private double _roll;
        private double _pitch;
        private double _yaw;
        private double _temperature;
        private string _updateTime;
        private string _rawHex;

        public byte SlaveId
        {
            get => _slaveId;
            set
            {
                _slaveId = value;
                OnPropertyChanged();
            }
        }

        public double Roll
        {
            get => _roll;
            set
            {
                _roll = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RollDisplay));
            }
        }

        public double Pitch
        {
            get => _pitch;
            set
            {
                _pitch = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PitchDisplay));
            }
        }

        public double Yaw
        {
            get => _yaw;
            set
            {
                _yaw = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(YawDisplay));
            }
        }

        public double Temperature
        {
            get => _temperature;
            set
            {
                _temperature = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TemperatureDisplay));
            }
        }

        public string UpdateTime
        {
            get => _updateTime;
            set
            {
                _updateTime = value;
                OnPropertyChanged();
            }
        }

        public string RawHex
        {
            get => _rawHex;
            set
            {
                _rawHex = value;
                OnPropertyChanged();
            }
        }

        public string RollDisplay => Roll.ToString("F4", CultureInfo.InvariantCulture);
        public string PitchDisplay => Pitch.ToString("F4", CultureInfo.InvariantCulture);
        public string YawDisplay => Yaw.ToString("F4", CultureInfo.InvariantCulture);
        public string TemperatureDisplay => Temperature.ToString("F4", CultureInfo.InvariantCulture);

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
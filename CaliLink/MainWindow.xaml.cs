using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WpfSerialTool
{
    public class SlaveData : INotifyPropertyChanged
    {
        private int slaveId;
        private string rollDisplay = "0.000";
        private string pitchDisplay = "0.000";
        private string yawDisplay = "0.000";
        private string tiltDisplay = "0.000";
        private string status = "垂直";
        private string temperatureDisplay = "0.000";
        private string updateTime = "";
        private string rawHex = "";

        public int SlaveId
        {
            get { return slaveId; }
            set
            {
                if (slaveId != value)
                {
                    slaveId = value;
                    OnPropertyChanged(nameof(SlaveId));
                }
            }
        }

        public string RollDisplay
        {
            get { return rollDisplay; }
            set
            {
                if (rollDisplay != value)
                {
                    rollDisplay = value;
                    OnPropertyChanged(nameof(RollDisplay));
                }
            }
        }

        public string PitchDisplay
        {
            get { return pitchDisplay; }
            set
            {
                if (pitchDisplay != value)
                {
                    pitchDisplay = value;
                    OnPropertyChanged(nameof(PitchDisplay));
                }
            }
        }

        public string YawDisplay
        {
            get { return yawDisplay; }
            set
            {
                if (yawDisplay != value)
                {
                    yawDisplay = value;
                    OnPropertyChanged(nameof(YawDisplay));
                }
            }
        }

        public string TiltDisplay
        {
            get { return tiltDisplay; }
            set
            {
                if (tiltDisplay != value)
                {
                    tiltDisplay = value;
                    OnPropertyChanged(nameof(TiltDisplay));
                }
            }
        }

        public string Status
        {
            get { return status; }
            set
            {
                if (status != value)
                {
                    status = value;
                    OnPropertyChanged(nameof(Status));
                }
            }
        }

        public string TemperatureDisplay
        {
            get { return temperatureDisplay; }
            set
            {
                if (temperatureDisplay != value)
                {
                    temperatureDisplay = value;
                    OnPropertyChanged(nameof(TemperatureDisplay));
                }
            }
        }

        public string UpdateTime
        {
            get { return updateTime; }
            set
            {
                if (updateTime != value)
                {
                    updateTime = value;
                    OnPropertyChanged(nameof(UpdateTime));
                }
            }
        }

        public string RawHex
        {
            get { return rawHex; }
            set
            {
                if (rawHex != value)
                {
                    rawHex = value;
                    OnPropertyChanged(nameof(RawHex));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChangedEventHandler handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    public partial class MainWindow : Window
    {
        private readonly SerialPort serialPort = new SerialPort();
        private readonly DispatcherTimer clockTimer = new DispatcherTimer();
        private readonly DispatcherTimer pollTimer = new DispatcherTimer();
        private readonly ObservableCollection<SlaveData> slaveDataList = new ObservableCollection<SlaveData>();
        private readonly List<byte> rxCache = new List<byte>();

        private bool isPolling = false;
        private int pollIndex = 0;

        private const int SlaveFrameLength = 22;
        private const byte SlaveFrameHead = 0xFA;

        public MainWindow()
        {
            InitializeComponent();
            InitializeUI();
        }

        private void InitializeUI()
        {
            dgSlaveData.ItemsSource = slaveDataList;

            clockTimer.Interval = TimeSpan.FromSeconds(1);
            clockTimer.Tick += delegate
            {
                txtClock.Text = DateTime.Now.ToString("HH:mm:ss");
            };
            clockTimer.Start();

            pollTimer.Tick += PollTimer_Tick;

            serialPort.DataReceived += SerialPort_DataReceived;
            serialPort.ReadTimeout = 500;
            serialPort.WriteTimeout = 500;

            tglTempResolution.Content = "0.01 ℃/LSB";

            SetLed(ledPort, Brushes.Gray);
            SetLed(ledCalibrate, Brushes.Gray);
            SetLed(ledSend, Brushes.Gray);
            SetLed(ledReceive, Brushes.Gray);

            txtPortStatus.Text = "未连接";
            txtCalibrateStatus.Text = "待机";

            RefreshPorts();
            AppendLog("系统", "界面初始化完成");
        }

        private void RefreshPorts()
        {
            string oldPort = cmbPorts.SelectedItem == null ? "" : cmbPorts.SelectedItem.ToString();

            cmbPorts.Items.Clear();

            string[] ports = SerialPort.GetPortNames();
            foreach (string port in ports)
            {
                cmbPorts.Items.Add(port);
            }

            if (!string.IsNullOrWhiteSpace(oldPort) && cmbPorts.Items.Contains(oldPort))
            {
                cmbPorts.SelectedItem = oldPort;
            }
            else if (cmbPorts.Items.Count > 0)
            {
                cmbPorts.SelectedIndex = 0;
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                int bytesToRead = serialPort.BytesToRead;
                if (bytesToRead <= 0)
                {
                    return;
                }

                byte[] buffer = new byte[bytesToRead];
                int readCount = serialPort.Read(buffer, 0, bytesToRead);

                if (readCount <= 0)
                {
                    return;
                }

                if (readCount != buffer.Length)
                {
                    Array.Resize(ref buffer, readCount);
                }

                string rawHex = ToHex(buffer);

                Dispatcher.Invoke(delegate
                {
                    FlashLed(ledReceive, Brushes.LimeGreen);
                    AppendLog("RX", "接收 " + readCount + " 字节: " + rawHex);

                    rxCache.AddRange(buffer);
                    ParseRxCache();
                });
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(delegate
                {
                    AppendLog("错误", "串口接收失败: " + ex.Message);
                });
            }
        }

        private void ParseRxCache()
        {
            while (rxCache.Count >= SlaveFrameLength)
            {
                int headIndex = rxCache.IndexOf(SlaveFrameHead);

                if (headIndex < 0)
                {
                    rxCache.Clear();
                    return;
                }

                if (headIndex > 0)
                {
                    rxCache.RemoveRange(0, headIndex);
                }

                if (rxCache.Count < SlaveFrameLength)
                {
                    return;
                }

                byte[] frame = rxCache.Take(SlaveFrameLength).ToArray();

                if (frame[0] != 0xFA || frame[2] != 0x03 || frame[3] != 0x10)
                {
                    rxCache.RemoveAt(0);
                    continue;
                }

                if (!CheckFrameCrc(frame))
                {
                    AppendLog("错误", "CRC校验失败，丢弃当前帧: " + ToHex(frame));
                    rxCache.RemoveAt(0);
                    continue;
                }

                rxCache.RemoveRange(0, SlaveFrameLength);
                ParseAndShowSlaveData(frame, ToHex(frame));
            }
        }

        private bool CheckFrameCrc(byte[] frame)
        {
            if (frame == null || frame.Length < SlaveFrameLength)
            {
                return false;
            }

            ushort crc = ModbusCrc16(frame, 20);
            byte crcHigh = (byte)(crc >> 8);
            byte crcLow = (byte)(crc & 0xFF);

            return frame[20] == crcHigh && frame[21] == crcLow;
        }

        private short ReadInt16BigEndian(byte high, byte low)
        {
            return unchecked((short)((high << 8) | low));
        }

        private ushort ReadUInt16BigEndian(byte high, byte low)
        {
            return unchecked((ushort)((high << 8) | low));
        }

        private double CombineSignedFixed3(short integerPart, ushort fractionalPart)
        {
            int raw12 = fractionalPart & 0x0FFF;

            if ((raw12 & 0x0800) != 0)
            {
                raw12 -= 0x1000;
            }

            return integerPart + raw12 / 1000.0;
        }

        private double CombineUnsignedFixed3(short integerPart, ushort fractionalPart)
        {
            return integerPart + fractionalPart / 1000.0;
        }

        private double GetTemperatureRegisterScale()
        {
            if (tglTempResolution.IsChecked == true)
            {
                return 0.01;
            }

            return 0.1;
        }

        private double DecodeTemperatureFromReg16ToReg19(short tempInt, ushort tempFrac)
        {
            double transmittedTemperature = CombineUnsignedFixed3(tempInt, tempFrac);

            if (transmittedTemperature < 200.0)
            {
                return transmittedTemperature - 100.0;
            }

            double scale = GetTemperatureRegisterScale();

            int rawAsUnsignedInt = (int)Math.Round(
                (transmittedTemperature - 100.0) / scale,
                MidpointRounding.AwayFromZero);

            ushort rawRegister = unchecked((ushort)rawAsUnsignedInt);
            short rawSigned = unchecked((short)rawRegister);

            return rawSigned * scale;
        }

        private double DegreesToRadians(double degrees)
        {
            return degrees * Math.PI / 180.0;
        }

        private double RadiansToDegrees(double radians)
        {
            return radians * 180.0 / Math.PI;
        }

        private double CalculateTiltAngle(double roll, double pitch)
        {
            double rollRad = DegreesToRadians(roll);
            double pitchRad = DegreesToRadians(pitch);

            double value = Math.Cos(rollRad) * Math.Cos(pitchRad);

            if (value > 1.0)
            {
                value = 1.0;
            }
            else if (value < -1.0)
            {
                value = -1.0;
            }

            return RadiansToDegrees(Math.Acos(value));
        }

        private string GetVerticalStatus(double tilt)
        {
            if (tilt < 13.0)
            {
                return "垂直";
            }

            return "不垂直";
        }

        private void ParseAndShowSlaveData(byte[] frame, string rawHex)
        {
            try
            {
                if (frame == null || frame.Length < SlaveFrameLength)
                {
                    return;
                }

                byte slaveId = frame[1];

                short rollInt = ReadInt16BigEndian(frame[4], frame[5]);
                ushort rollFrac = ReadUInt16BigEndian(frame[6], frame[7]);

                short pitchInt = ReadInt16BigEndian(frame[8], frame[9]);
                ushort pitchFrac = ReadUInt16BigEndian(frame[10], frame[11]);

                short yawInt = ReadInt16BigEndian(frame[12], frame[13]);
                ushort yawFrac = ReadUInt16BigEndian(frame[14], frame[15]);

                short tempInt = ReadInt16BigEndian(frame[16], frame[17]);
                ushort tempFrac = ReadUInt16BigEndian(frame[18], frame[19]);

                double roll = CombineSignedFixed3(rollInt, rollFrac);
                double pitch = CombineSignedFixed3(pitchInt, pitchFrac);
                double yaw = CombineSignedFixed3(yawInt, yawFrac);
                double temperature = DecodeTemperatureFromReg16ToReg19(tempInt, tempFrac);

                double tilt = CalculateTiltAngle(roll, pitch);
                string status = GetVerticalStatus(tilt);

                int digits = GetTemperatureRegisterScale() == 0.01 ? 2 : 1;

                AppendLog(
                    "解析",
                    "从站[" + slaveId + "] -> 横滚=" + roll.ToString("F3") +
                    "° 俯仰=" + pitch.ToString("F3") +
                    "° 航向=" + yaw.ToString("F3") +
                    "° 倾角=" + tilt.ToString("F3") +
                    "° 状态=" + status +
                    " 温度=" + Math.Round(
                                        temperature,
                                        digits,
                                        MidpointRounding.AwayFromZero
                                    ).ToString("F3") + "℃");

                UpdateSlaveDataGrid(slaveId, roll, pitch, yaw, tilt, status, temperature, rawHex);
            }
            catch (Exception ex)
            {
                AppendLog("错误", "数据解析失败: " + ex.Message);
            }
        }

        private void UpdateSlaveDataGrid(
            int slaveId,
            double roll,
            double pitch,
            double yaw,
            double tilt,
            string status,
            double temperature,
            string rawHex)
        {
            int digits = GetTemperatureRegisterScale() == 0.01 ? 2 : 1;
            SlaveData existing = slaveDataList.FirstOrDefault(s => s.SlaveId == slaveId);

            string temperatureText = Math.Round(
                                        temperature,
                                        digits,
                                        MidpointRounding.AwayFromZero
                                    ).ToString("F3");

            if (existing != null)
            {
                existing.RollDisplay = roll.ToString("F3");
                existing.PitchDisplay = pitch.ToString("F3");
                existing.YawDisplay = yaw.ToString("F3");
                existing.TiltDisplay = tilt.ToString("F3");
                existing.Status = status;
                existing.TemperatureDisplay = temperatureText;
                existing.UpdateTime = DateTime.Now.ToString("HH:mm:ss");
                existing.RawHex = rawHex;
            }
            else
            {
                slaveDataList.Add(new SlaveData
                {
                    SlaveId = slaveId,
                    RollDisplay = roll.ToString("F3"),
                    PitchDisplay = pitch.ToString("F3"),
                    YawDisplay = yaw.ToString("F3"),
                    TiltDisplay = tilt.ToString("F3"),
                    Status = status,
                    TemperatureDisplay = temperatureText,
                    UpdateTime = DateTime.Now.ToString("HH:mm:ss"),
                    RawHex = rawHex
                });
            }
        }

        private void AppendLog(string type, string message)
        {
            if (txtReceiveArea == null)
            {
                return;
            }

            string log = "[" + DateTime.Now.ToString("HH:mm:ss") + "] [" + type + "] " + message + Environment.NewLine;
            txtReceiveArea.AppendText(log);
            txtReceiveArea.ScrollToEnd();
        }

        private string ToHex(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                return "";
            }

            return BitConverter.ToString(data).Replace("-", " ");
        }

        private string GetComboBoxText(ComboBox comboBox)
        {
            if (comboBox.SelectedItem is ComboBoxItem)
            {
                ComboBoxItem item = (ComboBoxItem)comboBox.SelectedItem;
                return item.Content == null ? "" : item.Content.ToString();
            }

            if (comboBox.SelectedItem != null)
            {
                return comboBox.SelectedItem.ToString();
            }

            return comboBox.Text;
        }

        private bool TryGetSlaveId(out byte slaveId)
        {
            slaveId = 0;

            int id;
            if (!int.TryParse(txtSlaveId.Text.Trim(), out id))
            {
                AppendLog("错误", "从站地址不是有效数字");
                return false;
            }

            if (id < 1 || id > 247)
            {
                AppendLog("错误", "从站地址范围应为 1 到 247");
                return false;
            }

            slaveId = (byte)id;
            return true;
        }

        private bool TryGetPollInterval(out int interval)
        {
            interval = 500;

            if (!int.TryParse(txtPollInterval.Text.Trim(), out interval))
            {
                AppendLog("错误", "轮询间隔不是有效数字");
                return false;
            }

            if (interval < 100)
            {
                AppendLog("错误", "轮询间隔不能小于 100 ms");
                return false;
            }

            return true;
        }

        private List<byte> ParseSlaveList()
        {
            List<byte> result = new List<byte>();

            string text = txtSlaveList.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                AppendLog("错误", "从站列表为空");
                return result;
            }

            char[] separators = new char[] { ',', '，', ';', '；', ' ', '\t', '\r', '\n' };
            string[] parts = text.Split(separators, StringSplitOptions.RemoveEmptyEntries);

            foreach (string part in parts)
            {
                int id;
                if (!int.TryParse(part.Trim(), out id))
                {
                    AppendLog("错误", "从站列表包含无效地址: " + part);
                    continue;
                }

                if (id < 1 || id > 247)
                {
                    AppendLog("错误", "从站地址超出范围: " + id);
                    continue;
                }

                byte slaveId = (byte)id;
                if (!result.Contains(slaveId))
                {
                    result.Add(slaveId);
                }
            }

            return result;
        }

        private void SendBytes(byte[] data, string description)
        {
            if (!serialPort.IsOpen)
            {
                AppendLog("错误", "串口未打开");
                return;
            }

            try
            {
                serialPort.Write(data, 0, data.Length);
                FlashLed(ledSend, Brushes.DeepSkyBlue);
                AppendLog("TX", description + ": " + ToHex(data));
            }
            catch (Exception ex)
            {
                AppendLog("错误", "发送失败: " + ex.Message);
            }
        }

        private byte[] BuildQueryCommand(byte slaveId)
        {
            byte[] cmd = new byte[8];

            cmd[0] = slaveId;
            cmd[1] = 0x03;
            cmd[2] = 0x0B;
            cmd[3] = 0x00;
            cmd[4] = 0x00;
            cmd[5] = 0x08;

            ushort crc = ModbusCrc16(cmd, 6);
            cmd[6] = (byte)(crc >> 8);
            cmd[7] = (byte)(crc & 0xFF);

            return cmd;
        }

        private ushort ModbusCrc16(byte[] data, int length)
        {
            ushort crc = 0xFFFF;

            for (int i = 0; i < length; i++)
            {
                crc ^= data[i];

                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc = (ushort)((crc >> 1) ^ 0xA001);
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }

            return unchecked((ushort)(((crc & 0x00FF) << 8) | ((crc >> 8) & 0x00FF)));
        }

        private void SetLed(Ellipse led, Brush brush)
        {
            if (led != null)
            {
                led.Fill = brush;
            }
        }

        private void FlashLed(Ellipse led, Brush flashBrush)
        {
            if (led == null)
            {
                return;
            }

            led.Fill = flashBrush;

            DispatcherTimer timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(180);
            timer.Tick += delegate
            {
                timer.Stop();
                led.Fill = Brushes.Gray;
            };
            timer.Start();
        }

        private void SetPortOpenedUi(bool opened)
        {
            if (opened)
            {
                btnOpenClose.Content = "关闭串口";

                btnQuerySlave.IsEnabled = true;
                btnSlaveMinus.IsEnabled = true;
                btnSlavePlus.IsEnabled = true;
                btnStartCalibrate.IsEnabled = true;
                btnStopCalibrate.IsEnabled = true;
                btnStartPolling.IsEnabled = true;
                btnStopPolling.IsEnabled = false;

                cmbPorts.IsEnabled = false;
                cmbBaudRate.IsEnabled = false;

                txtPortStatus.Text = "已连接";
                txtPortStatus.Foreground = Brushes.LimeGreen;
                SetLed(ledPort, Brushes.LimeGreen);
            }
            else
            {
                btnOpenClose.Content = "打开串口";

                btnQuerySlave.IsEnabled = false;
                btnSlaveMinus.IsEnabled = false;
                btnSlavePlus.IsEnabled = false;
                btnStartCalibrate.IsEnabled = false;
                btnStopCalibrate.IsEnabled = false;
                btnStartPolling.IsEnabled = false;
                btnStopPolling.IsEnabled = false;

                cmbPorts.IsEnabled = true;
                cmbBaudRate.IsEnabled = true;

                txtPortStatus.Text = "未连接";
                txtPortStatus.Foreground = Brushes.Gray;
                SetLed(ledPort, Brushes.Gray);

                txtCalibrateStatus.Text = "待机";
                txtCalibrateStatus.Foreground = Brushes.Gray;
                SetLed(ledCalibrate, Brushes.Gray);
            }
        }

        private void tglTempResolution_Checked(object sender, RoutedEventArgs e)
        {
            if (tglTempResolution != null)
            {
                tglTempResolution.Content = "0.01 ℃/LSB";
            }

            if (IsLoaded)
            {
                AppendLog("系统", "温度转换倍率切换为 0.01 ℃/LSB");
            }
        }

        private void tglTempResolution_Unchecked(object sender, RoutedEventArgs e)
        {
            if (tglTempResolution != null)
            {
                tglTempResolution.Content = "0.1 ℃/LSB";
            }

            if (IsLoaded)
            {
                AppendLog("系统", "温度转换倍率切换为 0.1 ℃/LSB");
            }
        }

        private void btnRefreshPorts_Click(object sender, RoutedEventArgs e)
        {
            RefreshPorts();
            AppendLog("系统", "串口列表已刷新");
        }

        private void btnOpenClose_Click(object sender, RoutedEventArgs e)
        {
            if (!serialPort.IsOpen)
            {
                try
                {
                    string portName = cmbPorts.SelectedItem == null ? "" : cmbPorts.SelectedItem.ToString();
                    string baudText = GetComboBoxText(cmbBaudRate);

                    if (string.IsNullOrWhiteSpace(portName))
                    {
                        AppendLog("错误", "未选择串口");
                        return;
                    }

                    int baudRate;
                    if (!int.TryParse(baudText, out baudRate))
                    {
                        AppendLog("错误", "波特率无效");
                        return;
                    }

                    serialPort.PortName = portName;
                    serialPort.BaudRate = baudRate;
                    serialPort.DataBits = 8;
                    serialPort.StopBits = StopBits.One;
                    serialPort.Parity = Parity.None;
                    serialPort.Handshake = Handshake.None;

                    serialPort.Open();

                    rxCache.Clear();
                    SetPortOpenedUi(true);

                    AppendLog("系统", "串口已打开: " + portName + ", " + baudRate + ", 8N1");
                }
                catch (Exception ex)
                {
                    AppendLog("错误", "打开串口失败: " + ex.Message);
                }
            }
            else
            {
                try
                {
                    pollTimer.Stop();
                    isPolling = false;

                    serialPort.Close();

                    rxCache.Clear();
                    SetPortOpenedUi(false);

                    AppendLog("系统", "串口已关闭");
                }
                catch (Exception ex)
                {
                    AppendLog("错误", "关闭串口失败: " + ex.Message);
                }
            }
        }

        private void btnQuerySlave_Click(object sender, RoutedEventArgs e)
        {
            byte slaveId;
            if (!TryGetSlaveId(out slaveId))
            {
                return;
            }

            byte[] cmd = BuildQueryCommand(slaveId);
            SendBytes(cmd, "发送单站问询命令");
        }

        private void btnStartCalibrate_Click(object sender, RoutedEventArgs e)
        {
            byte[] cmd = new byte[] { 0xFF, 0xAA, 0xFF };
            SendBytes(cmd, "发送开始校准命令");

            txtCalibrateStatus.Text = "校准中";
            txtCalibrateStatus.Foreground = Brushes.Orange;
            SetLed(ledCalibrate, Brushes.Orange);
        }

        private void btnStopCalibrate_Click(object sender, RoutedEventArgs e)
        {
            byte[] cmd = new byte[] { 0xAA, 0xFF, 0xFF };
            SendBytes(cmd, "发送停止校准命令");

            txtCalibrateStatus.Text = "待机";
            txtCalibrateStatus.Foreground = Brushes.Gray;
            SetLed(ledCalibrate, Brushes.Gray);
        }

        private void btnSlaveMinus_Click(object sender, RoutedEventArgs e)
        {
            int id;
            if (int.TryParse(txtSlaveId.Text.Trim(), out id) && id > 1)
            {
                txtSlaveId.Text = (id - 1).ToString();
            }
        }

        private void btnSlavePlus_Click(object sender, RoutedEventArgs e)
        {
            int id;
            if (int.TryParse(txtSlaveId.Text.Trim(), out id) && id < 247)
            {
                txtSlaveId.Text = (id + 1).ToString();
            }
        }

        private void btnClearLog_Click(object sender, RoutedEventArgs e)
        {
            txtReceiveArea.Clear();
        }

        private void btnStartPolling_Click(object sender, RoutedEventArgs e)
        {
            if (!serialPort.IsOpen)
            {
                AppendLog("错误", "串口未打开");
                return;
            }

            int interval;
            if (!TryGetPollInterval(out interval))
            {
                return;
            }

            List<byte> slaveList = ParseSlaveList();
            if (slaveList.Count == 0)
            {
                AppendLog("错误", "没有有效的轮询从站地址");
                return;
            }

            pollIndex = 0;
            pollTimer.Interval = TimeSpan.FromMilliseconds(interval);
            pollTimer.Start();

            isPolling = true;
            btnStartPolling.IsEnabled = false;
            btnStopPolling.IsEnabled = true;

            AppendLog("系统", "开始批量轮询，间隔 " + interval + " ms，从站数量 " + slaveList.Count);
        }

        private void btnStopPolling_Click(object sender, RoutedEventArgs e)
        {
            pollTimer.Stop();

            isPolling = false;
            btnStartPolling.IsEnabled = serialPort.IsOpen;
            btnStopPolling.IsEnabled = false;

            AppendLog("系统", "停止批量轮询");
        }

        private void PollTimer_Tick(object sender, EventArgs e)
        {
            if (!serialPort.IsOpen)
            {
                pollTimer.Stop();
                isPolling = false;
                btnStartPolling.IsEnabled = false;
                btnStopPolling.IsEnabled = false;
                AppendLog("错误", "串口已关闭，轮询停止");
                return;
            }

            List<byte> slaveList = ParseSlaveList();
            if (slaveList.Count == 0)
            {
                return;
            }

            if (pollIndex >= slaveList.Count)
            {
                pollIndex = 0;
            }

            byte slaveId = slaveList[pollIndex];
            pollIndex++;

            byte[] cmd = BuildQueryCommand(slaveId);
            SendBytes(cmd, "轮询从站[" + slaveId + "]");
        }

        protected override void OnClosed(EventArgs e)
        {
            try
            {
                pollTimer.Stop();

                if (serialPort.IsOpen)
                {
                    serialPort.Close();
                }
            }
            catch
            {
            }

            base.OnClosed(e);
        }
    }
}
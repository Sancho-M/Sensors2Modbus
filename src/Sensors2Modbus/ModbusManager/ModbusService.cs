using NModbus.Data;
using NModbus.Serial;
using NModbus;
using System.IO.Ports;
class ModbusService : IModbusService
{
    private Task? _modbusTask;
    private SerialPort? _port;
    private SlaveDataStore _dataStore = new();
    private AppSettings _appSettings = new();
    public UInt16 DALAY_BETWEEN_REQUEST { get; set; } = 100;

    public event Action<bool, bool>? StatusChanged;

    private readonly object _lockObject = new object();
    public DateTime errorTime { get; set; } = DateTime.MinValue;

    private CancellationToken _parentToken;
    private CancellationTokenSource? _internalCts;

    private bool _isWorking;
    public bool IsWorking
    {
        get => _isWorking;
        private set
        {
            var old = _isWorking;
            _isWorking = value;
            StatusChanged?.Invoke(old, value);
        }
    }

    public ModbusService(CancellationToken parentToken)
    {
        _parentToken = parentToken;
    }

    public SlaveDataStore GetDataStore() => _dataStore;

    public async Task StartAsync()
    {
        await StopAsync(); 

        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(_parentToken);
        _modbusTask = MonitorLoopAsync(_internalCts.Token);
        await Task.Yield();
    }

    private async Task MonitorLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await InitAsync(token);
                await Task.Delay(DALAY_BETWEEN_REQUEST, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (IsWorking || errorTime == DateTime.MinValue)
                {
                    errorTime = DateTime.Now;
                    ChangeModbusStatus(false, $"MODBUS ERROR: {ex.Message}. Error date: {DateTime.Now:HH:mm:ss}");
                }

                IsWorking = false;

                try
                {
                    await Task.Delay(DALAY_BETWEEN_REQUEST, token);
                }
                catch { break; }
            }
        }
    }
    private async Task InitAsync(CancellationToken token)
    {
        _port = new SerialPort(_appSettings.COM_PORT, _appSettings.BOUDRATE, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = DALAY_BETWEEN_REQUEST,
            WriteTimeout = DALAY_BETWEEN_REQUEST,
            ReceivedBytesThreshold = 8
        };
        _port.Open();

        var factory = new ModbusFactory();
        var adapter = new SerialPortAdapter(_port);
        var network = factory.CreateRtuSlaveNetwork(adapter);
        var slave = factory.CreateSlave(_appSettings.SLAVE_ID, _dataStore);
        network.AddSlave(slave);

        ChangeModbusStatus(true, $"Successfully connected to {_port.PortName}. Connection date: {DateTime.Now:HH:mm:ss}");

        IsWorking = true;

        await Task.Yield();
        await Task.Run(() => network.ListenAsync(token));

    }

    public void UpdateRegisters(ushort[] reg)
    {
        if (reg == null) return;

        lock (_lockObject)
        {
            _dataStore.HoldingRegisters.WritePoints(0, reg);
        }
    }

    public async Task StopAsync()
    {
        if (_internalCts == null)
            return;

        try
        {
            _internalCts.Cancel();

            if (_modbusTask != null)
            {
                try
                {
                    await _modbusTask; // дождаться завершения ListenAsync
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"Modbus stop error: {ex.Message}");

                }
            }
        }
        finally
        {
            try { _port?.Close(); } catch { }
            try { _port?.Dispose(); } catch { }

            _modbusTask = null;
            IsWorking = false;

            _internalCts.Dispose();
            _internalCts = null;
        }
    }

    private void ChangeModbusStatus(bool status, string message)
    {
        _ = status ? Console.ForegroundColor = ConsoleColor.Green : Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }
}

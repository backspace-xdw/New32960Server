using System.Collections.Concurrent;
using InfluxDB.Client;
using InfluxDB.Client.Api.Domain;
using InfluxDB.Client.Writes;
using GB32960.Protocol;
using GB32960.Protocol.DataTypes;
using Microsoft.Extensions.Logging;

namespace GB32960.Server;

/// <summary>
/// InfluxDB 数据存储服务
/// 批量异步写入，不阻塞消息处理主线程
/// </summary>
public class InfluxDbStore : IDisposable
{
    private readonly ILogger<InfluxDbStore> _logger;
    private readonly InfluxDbConfig _config;
    private InfluxDBClient? _client;
    private WriteApiAsync? _writeApi;
    private readonly ConcurrentQueue<PointData> _queue = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _flushTask;
    private long _totalWrites;
    private long _totalErrors;

    public long TotalWrites => Interlocked.Read(ref _totalWrites);
    public long TotalErrors => Interlocked.Read(ref _totalErrors);
    public int QueueSize => _queue.Count;

    public InfluxDbStore(ILogger<InfluxDbStore> logger, InfluxDbConfig config)
    {
        _logger = logger;
        _config = config;
    }

    public bool Start()
    {
        if (!_config.Enabled)
        {
            _logger.LogInformation("InfluxDB 存储已禁用");
            return false;
        }

        try
        {
            _client = new InfluxDBClient(_config.Url, _config.Token);
            _writeApi = _client.GetWriteApiAsync();

            // 启动后台批量写入任务
            _flushTask = Task.Run(FlushLoop);

            _logger.LogInformation("InfluxDB 已连接: {url}, Bucket={bucket}, Org={org}",
                _config.Url, _config.Bucket, _config.Org);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InfluxDB 连接失败: {url}", _config.Url);
            return false;
        }
    }

    /// <summary>写入车辆登入事件</summary>
    public void WriteVehicleLogin(string vin, VehicleLoginData data)
    {
        var point = PointData.Measurement("vehicle_event")
            .Tag("vin", vin)
            .Tag("event", "login")
            .Field("iccid", data.ICCID)
            .Field("login_seq", data.LoginSequence)
            .Field("subsystem_count", data.SubsystemCount)
            .Timestamp(data.CollectionTime, WritePrecision.S);
        Enqueue(point);
    }

    /// <summary>写入车辆登出事件</summary>
    public void WriteVehicleLogout(string vin, VehicleLogoutData data)
    {
        var point = PointData.Measurement("vehicle_event")
            .Tag("vin", vin)
            .Tag("event", "logout")
            .Field("logout_seq", data.LogoutSequence)
            .Timestamp(data.CollectionTime, WritePrecision.S);
        Enqueue(point);
    }

    /// <summary>写入实时/补发数据的所有信息体</summary>
    public void WriteRealtimeData(string vin, DateTime time, List<IRealtimeInfoItem> items)
    {
        foreach (var item in items)
        {
            switch (item)
            {
                case VehicleData vd:
                    WriteVehicleData(vin, time, vd);
                    break;
                case DriveMotorData dm:
                    WriteMotorData(vin, time, dm);
                    break;
                case FuelCellData fc:
                    WriteFuelCellData(vin, time, fc);
                    break;
                case EngineData eng:
                    WriteEngineData(vin, time, eng);
                    break;
                case VehiclePositionData pos:
                    WritePositionData(vin, time, pos);
                    break;
                case ExtremeValueData ev:
                    WriteExtremeData(vin, time, ev);
                    break;
                case AlarmData alarm:
                    WriteAlarmData(vin, time, alarm);
                    break;
                case BatteryVoltageData bv:
                    WriteBatteryVoltageData(vin, time, bv);
                    break;
                case BatteryTemperatureData bt:
                    WriteBatteryTempData(vin, time, bt);
                    break;
            }
        }
    }

    // ─── 各信息体写入 ────────────────────────────

    private void WriteVehicleData(string vin, DateTime time, VehicleData vd)
    {
        var point = PointData.Measurement("vehicle_data")
            .Tag("vin", vin)
            .Tag("status", vd.Status.ToString())
            .Tag("charge_state", vd.ChargeState.ToString())
            .Tag("drive_mode", vd.DriveMode.ToString())
            .Field("speed", vd.GetSpeedKmh())
            .Field("mileage", vd.GetMileageKm())
            .Field("total_voltage", vd.GetVoltageV())
            .Field("total_current", vd.GetCurrentA())
            .Field("soc", (int)vd.SOC)
            .Field("dcdc_status", (int)vd.DcDcStatus)
            .Field("gear", (int)vd.Gear)
            .Field("insulation_resistance", (int)vd.InsulationResistance)
            .Field("accelerator", (int)vd.AcceleratorPedal)
            .Field("brake", (int)vd.BrakePedal)
            .Timestamp(time, WritePrecision.S);
        Enqueue(point);
    }

    private void WriteMotorData(string vin, DateTime time, DriveMotorData dm)
    {
        foreach (var m in dm.Motors)
        {
            var point = PointData.Measurement("drive_motor")
                .Tag("vin", vin)
                .Tag("motor_seq", m.Sequence.ToString())
                .Tag("state", m.State.ToString())
                .Field("controller_temp", m.GetControllerTempC())
                .Field("rpm", m.GetRPM())
                .Field("torque", m.Torque == 0xFFFF ? 0 : (m.Torque - 20000) / 10.0)
                .Field("motor_temp", m.GetMotorTempC())
                .Field("controller_voltage", m.ControllerVoltage == 0xFFFF ? 0 : m.ControllerVoltage / 10.0)
                .Field("controller_current", m.ControllerCurrent == 0xFFFF ? 0 : (m.ControllerCurrent - 10000) / 10.0)
                .Timestamp(time, WritePrecision.S);
            Enqueue(point);
        }
    }

    private void WriteFuelCellData(string vin, DateTime time, FuelCellData fc)
    {
        var point = PointData.Measurement("fuel_cell")
            .Tag("vin", vin)
            .Field("voltage", fc.Voltage / 10.0)
            .Field("current", fc.Current / 10.0)
            .Field("consumption_rate", fc.ConsumptionRate / 100.0)
            .Field("probe_count", fc.TempProbeCount)
            .Field("max_h_temp", fc.MaxHydrogenTemp / 10.0 - 40)
            .Field("max_h_concentration", (int)fc.MaxHydrogenConcentration)
            .Field("h_pressure", fc.HydrogenPressure / 10.0)
            .Timestamp(time, WritePrecision.S);
        Enqueue(point);
    }

    private void WriteEngineData(string vin, DateTime time, EngineData eng)
    {
        var point = PointData.Measurement("engine")
            .Tag("vin", vin)
            .Tag("state", eng.State.ToString())
            .Field("rpm", (int)eng.CrankshaftRPM)
            .Field("fuel_rate", eng.FuelConsumptionRate / 100.0)
            .Timestamp(time, WritePrecision.S);
        Enqueue(point);
    }

    private void WritePositionData(string vin, DateTime time, VehiclePositionData pos)
    {
        var point = PointData.Measurement("vehicle_position")
            .Tag("vin", vin)
            .Tag("valid", pos.IsValid.ToString())
            .Field("longitude", pos.GetLongitude())
            .Field("latitude", pos.GetLatitude())
            .Timestamp(time, WritePrecision.S);
        Enqueue(point);
    }

    private void WriteExtremeData(string vin, DateTime time, ExtremeValueData ev)
    {
        var point = PointData.Measurement("extreme_values")
            .Tag("vin", vin)
            .Field("max_voltage", ev.GetMaxVoltageV())
            .Field("min_voltage", ev.GetMinVoltageV())
            .Field("max_voltage_subsystem", (int)ev.MaxVoltageSubsystem)
            .Field("max_voltage_cell", (int)ev.MaxVoltageCellIndex)
            .Field("min_voltage_subsystem", (int)ev.MinVoltageSubsystem)
            .Field("min_voltage_cell", (int)ev.MinVoltageCellIndex)
            .Field("max_temp", ev.GetMaxTempC())
            .Field("min_temp", ev.GetMinTempC())
            .Timestamp(time, WritePrecision.S);
        Enqueue(point);
    }

    private void WriteAlarmData(string vin, DateTime time, AlarmData alarm)
    {
        var point = PointData.Measurement("alarm")
            .Tag("vin", vin)
            .Tag("level", alarm.MaxAlarmLevel.ToString())
            .Field("general_flags", (long)alarm.GeneralAlarmFlags)
            .Field("battery_fault_count", (int)alarm.BatteryFaultCount)
            .Field("motor_fault_count", (int)alarm.MotorFaultCount)
            .Field("engine_fault_count", (int)alarm.EngineFaultCount)
            .Field("other_fault_count", (int)alarm.OtherFaultCount)
            .Timestamp(time, WritePrecision.S);
        Enqueue(point);
    }

    private void WriteBatteryVoltageData(string vin, DateTime time, BatteryVoltageData bv)
    {
        foreach (var sub in bv.Subsystems)
        {
            var point = PointData.Measurement("battery_voltage")
                .Tag("vin", vin)
                .Tag("subsystem", sub.SubsystemNumber.ToString())
                .Field("voltage", sub.SubsystemVoltage / 10.0)
                .Field("current", (sub.SubsystemCurrent - 10000) / 10.0)
                .Field("total_cells", (int)sub.TotalCellCount)
                .Field("frame_cells", (int)sub.FrameCellCount)
                .Timestamp(time, WritePrecision.S);
            Enqueue(point);

            // 单体电压（高频数据量大，可选写入）
            if (_config.WriteCellVoltages && sub.CellVoltages.Count > 0)
            {
                for (int i = 0; i < sub.CellVoltages.Count; i++)
                {
                    var cellPoint = PointData.Measurement("cell_voltage")
                        .Tag("vin", vin)
                        .Tag("subsystem", sub.SubsystemNumber.ToString())
                        .Tag("cell", (sub.FrameStartCellIndex + i).ToString())
                        .Field("voltage", sub.CellVoltages[i] / 1000.0)
                        .Timestamp(time, WritePrecision.S);
                    Enqueue(cellPoint);
                }
            }
        }
    }

    private void WriteBatteryTempData(string vin, DateTime time, BatteryTemperatureData bt)
    {
        foreach (var sub in bt.Subsystems)
        {
            var point = PointData.Measurement("battery_temperature")
                .Tag("vin", vin)
                .Tag("subsystem", sub.SubsystemNumber.ToString())
                .Field("probe_count", (int)sub.ProbeCount)
                .Field("avg_temp", sub.ProbeTemperatures.Count > 0
                    ? sub.ProbeTemperatures.Average(t => t - 40) : 0)
                .Field("max_temp", sub.ProbeTemperatures.Count > 0
                    ? sub.ProbeTemperatures.Max(t => t - 40) : 0)
                .Field("min_temp", sub.ProbeTemperatures.Count > 0
                    ? sub.ProbeTemperatures.Min(t => t - 40) : 0)
                .Timestamp(time, WritePrecision.S);
            Enqueue(point);
        }
    }

    // ─── 队列和批量写入 ──────────────────────────

    private void Enqueue(PointData point)
    {
        _queue.Enqueue(point);
    }

    private async Task FlushLoop()
    {
        var batch = new List<PointData>();

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                // 收集一批数据
                batch.Clear();
                while (batch.Count < _config.BatchSize && _queue.TryDequeue(out var point))
                    batch.Add(point);

                if (batch.Count > 0 && _writeApi != null)
                {
                    await _writeApi.WritePointsAsync(batch, _config.Bucket, _config.Org);
                    Interlocked.Add(ref _totalWrites, batch.Count);
                }
                else
                {
                    // 队列空，等待
                    await Task.Delay(_config.FlushIntervalMs, _cts.Token).ContinueWith(_ => { });
                }
            }
            catch (Exception ex)
            {
                Interlocked.Add(ref _totalErrors, batch.Count);
                _logger.LogError(ex, "InfluxDB 批量写入失败, 丢弃 {count} 条", batch.Count);
                await Task.Delay(1000); // 出错后短暂等待
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _flushTask?.Wait(3000);
        _client?.Dispose();
    }
}

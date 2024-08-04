using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using volkswagen_weconnect.Dtos;

namespace volkswagen_weconnect;

public class VwService
{
    private readonly VwConnection _vwConnection;
    private readonly ILogger<VwService> _logger;

    public VwService(VwConnection vwConnection, ILoggerFactory loggerFactory)
    {
        _vwConnection = vwConnection;
        _logger = loggerFactory.CreateLogger<VwService>();
    }

    public List<Vehicle> GetVehicles()
    {
        DataWrapper data = _vwConnection.RequestVwBackend<DataWrapper>("/vehicle/v2/vehicles");
        _logger.LogInformation("Found {Count} vehicles", data.Data.Count);
        return data.Data;
    }

    public Charge GetChargeData(Vehicle vehicle)
    {
        _logger.LogInformation("Getting charge data for {Vin}", vehicle.Vin);
        ChargingWrapper chargeWrapper = _vwConnection.RequestVwBackend<ChargingWrapper>($"/vehicle/v1/vehicles/{vehicle.Vin}/selectivestatus?jobs=charging");
        return new Charge(
            chargeWrapper.Charging.BatteryStatus.Value.CurrentSoc,
            chargeWrapper.Charging.BatteryStatus.Value.CruisingRangeKm,
            chargeWrapper.Charging.ChargingStatus.Value.ChargingState,
            chargeWrapper.Charging.ChargingStatus.Value.ChargePowerKw,
            chargeWrapper.Charging.PlugStatus.Value.PlugConnectionState);
    }

    #region Wrapper objects from VW backend

    private class DataWrapper
    {
        public List<Vehicle> Data { get; set; }
    }

    private class ChargingWrapper
    {
        public Charging Charging { get; set; }
    }

    private class Charging
    {
        public BatteryStatus BatteryStatus { get; set; }
        public ChargingStatus ChargingStatus { get; set; }
        public PlugStatus PlugStatus { get; set; }
    }

    private class BatteryStatus
    {
        public BatteryStatusValue Value { get; set; }
    }

    private class BatteryStatusValue
    {
        [JsonPropertyName("currentSOC_pct")] public int CurrentSoc { get; set; }

        [JsonPropertyName("cruisingRangeElectric_km")]
        public int CruisingRangeKm { get; set; }
    }

    private class ChargingStatus
    {
        public ChargingStatusValue Value { get; set; }
    }

    private class ChargingStatusValue
    {
        public string ChargingState { get; set; }

        [JsonPropertyName("chargePower_kW")] public decimal ChargePowerKw { get; set; }
    }

    private class PlugStatus
    {
        public PlugStatusValue Value { get; set; }
    }

    private class PlugStatusValue
    {
        public string PlugConnectionState { get; set; }
    }

    #endregion
}
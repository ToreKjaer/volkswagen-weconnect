namespace volkswagen_weconnect.Dtos;

public class Charge
{
    public int CurrentSoc { get; set; }
    public int CruisingRangeKm { get; set; }
    public string ChargingState { get; set; }
    public decimal ChargePowerKw { get; set; }
    public bool IsPlugConnected { get; set; }

    public Charge()
    {
    }

    public Charge(int currentSoc, int cruisingRangeKm, string chargingState, decimal chargePowerKw, string plugConnectionState)
    {
        CurrentSoc = currentSoc;
        CruisingRangeKm = cruisingRangeKm;
        ChargingState = chargingState;
        ChargePowerKw = chargePowerKw;
        IsPlugConnected = plugConnectionState == "connected";
    }
}
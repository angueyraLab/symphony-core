// src/symphony-core/Symphony.ExternalDevices/AMSystemsInterop.cs
using System;

namespace Symphony.ExternalDevices
{
    public static class AMSystemsInterop
    {
        public class AMSystemsData
        {
            public double Capacitance { get; set; }
            public double Frequency { get; set; }
            public double Gain { get; set; }
            public OperatingMode OperatingMode { get; set; }
            public double ExternalCommandSensitivity { get; set; }
            public ExternalCommandSensitivityUnits ExternalCommandSensitivityUnits { get; set; }
            public boolean Overload { get; set; }
            public override string ToString()
            {
                return String.Format("{{ OperatingMode={0}, Gain={1}, ... }}", OperatingMode, Gain);
            }
        }

        public enum OperatingMode
        {
            Vtest,
            VComp,
            VClamp,
            I0,
            IClamp,
            IResist,
            IFollow
        }

        public enum ExternalCommandSensitivityUnits
        {
            V_V,
            A_V,
            OFF
        }
    }
}
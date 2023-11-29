// src/symphony-core/Symphony.ExternalDevices/AMSystems2400.cs
// Created: Oct 17, 2023 (Angueyra)

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Symphony.Core;

namespace Symphony.ExternalDevices
{
    public class AMSystems2400 : IAMSystems
    {
        public double Beta { get; private set; }

        public AMSystems2400()
        {
            Beta = 1;
            ProbeGain = 'Low'
            Rfeedback_Lo = 100e6; // Depends on headstage and Probe Gain: for LOW = 100 MOhm
            Rfeedback_Hi = 1e9; // Depends on headstage and Probe Gain: for HIGH = 1 GOhm
            ExternalScaling = 50; // Depends on switch
        }

// Telegraph outputs include: 
    // Noise
    // Freq
    // Mode
    // Error
    // Gain
    // Cmembrane

        public AMSystemsInterop.AMSystemsData ReadTelegraphData(IDictionary<string, IInputData> data)
        {
            var telegraph = new AMSystemsInterop.AMSystemsData();

            if (data.ContainsKey(AMSystemsDevice.FREQUENCY_TELEGRAPH_STREAM_NAME))
            {
                var voltage = ReadVoltage(data[AMSystemsDevice.FREQUENCY_TELEGRAPH_STREAM_NAME]);
                telegraph.Frequency = _voltageToFrequency[Math.Round(voltage * 20) / 20];
            }

            if (data.ContainsKey(AMSystemsDevice.GAIN_TELEGRAPH_STREAM_NAME))
            {
                var voltage = ReadVoltage(data[AMSystemsDevice.GAIN_TELEGRAPH_STREAM_NAME]);
                telegraph.Gain = _voltageToGain[Math.Round(voltage * 20) / 20];
            }

            if (data.ContainsKey(AMSystemsDevice.OVERLOAD_TELEGRAPH_STREAM_NAME))
            {
                var voltage = ReadVoltage(data[AMSystemsDevice.OVERLOAD_TELEGRAPH_STREAM_NAME]);
                telegraph.Overload = _voltageToOverload[Math.Round(voltage * 20) / 20];
            }

            if (data.ContainsKey(AMSystemsDevice.MODE_TELEGRAPH_STREAM_NAME))
            {
                var voltage = ReadVoltage(data[AMSystemsDevice.MODE_TELEGRAPH_STREAM_NAME]);
                telegraph.OperatingMode = _voltageToMode[Math.Round(voltage * 20) / 20];
            }

            switch (telegraph.OperatingMode)
            {
                case AMSystemsInterop.OperatingMode.IResist:
                case AMSystemsInterop.OperatingMode.IFollow:
                case AMSystemsInterop.OperatingMode.IClamp:
                    // telegraph.ExternalCommandSensitivity = 2e-9/Beta; //In Axopatch, I_command sensitivity is 2/Beta nA/V (front switched)
                    // In AMSystems4500, lower feedback resistor and probe gain determine current clamp external sensitivity
                    if (ProbeGain == 'Low')
                    {
                        telegraph.ExternalCommandSensitivity = 1e-9/(ExternalScaling*Rfeedback_Lo);
                    }
                    else if (ProbeGain == 'High')
                    {
                        telegraph.ExternalCommandSensitivity = 1e-9/(10*ExternalScaling*Rfeedback_Lo);
                    }
                    telegraph.ExternalCommandSensitivityUnits = AMSystemsInterop.ExternalCommandSensitivityUnits.A_V;
                    break;
                case AMSystemsInterop.OperatingMode.VTest:
                case AMSystemsInterop.OperatingMode.VComp:
                case AMSystemsInterop.OperatingMode.VClamp:
                    // telegraph.ExternalCommandSensitivity = 0.02; //In Axopatch, V_command sensitivity is 20 mV/V (front switched)
                    telegraph.ExternalCommandSensitivity = 1/ExternalScaling; // In AMSystems4500: for /10: 100mV/V; for /50: 20mV/V
                    telegraph.ExternalCommandSensitivityUnits = AMSystemsInterop.ExternalCommandSensitivityUnits.V_V;
                    break;
                case AMSystemsInterop.OperatingMode.I0:
                    telegraph.ExternalCommandSensitivityUnits = AMSystemsInterop.ExternalCommandSensitivityUnits.OFF;
                    break;
            }

            return telegraph;
        }

        private static decimal ReadVoltage(IInputData data)
        {
            var measurements = data.DataWithUnits("V").Data;
            return measurements.First().Quantity;
        }

        // Cmembrane: Simple conversion: 10pF/V
        // Noise: Do not understand this output yet; seems to be an rms calculation on probe output, which depends on Rf

        // Error: Overload
        private readonly IDictionary<decimal, bool> _voltageToOverload = new Dictionary<decimal, bool>
        {
            {0m, false},
            {2.5, true},
        };
        
        // Freq (in kHz)
        private readonly IDictionary<decimal, double> _voltageToFrequency = new Dictionary<decimal, double>
        {
            {0m, 0.5},
            {1.25m, 1},
            {2.5m, 2},
            {3.75m, 5},
            {5m, 10},
            {6.25m, 20},
            {7.25m, 100},
        };
        
        // Gain
        private readonly IDictionary<decimal, double> _voltageToGain = new Dictionary<decimal, double>
        {
            {0m, 0.01},
            {0.3m, 0.02},
            {0.8m, 0.05},
            {1.3m, 0.1},
            {1.8m, 0.2},
            {2.3m, 0.5},
            {2.8m, 1},
            {3.3m, 2},
            {3.8m, 5},
            {4.3m, 10},
            {4.8m, 20},
            {5.3m, 50},
            {5.8m, 100},
            {6.3m, 200},
            {6.8m, 500},
            {7.3m, 1000},
            {8.3m, 2000},
            {8.8m, 5000},
        };
        
        // Mode
        private readonly IDictionary<decimal, AMSystemsInterop.OperatingMode> _voltageToMode = new Dictionary<decimal, AMSystemsInterop.OperatingMode>
        {
            {0m, AMSystemsInterop.OperatingMode.Vtest},
            {0.8m, AMSystemsInterop.OperatingMode.VComp},
            {1.8m, AMSystemsInterop.OperatingMode.VClamp},
            {2.8m, AMSystemsInterop.OperatingMode.I0},
            {3.8m, AMSystemsInterop.OperatingMode.IClamp}
            {4.8m, AMSystemsInterop.OperatingMode.IResist}
            {5.8m, AMSystemsInterop.OperatingMode.IFollow}
        };
    }
}
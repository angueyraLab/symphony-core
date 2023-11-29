// src/symphony-core/Symphony.ExternalDevices/AMSystemsDevice.cs
// Created: Oct 17, 2023 (Angueyra)

using System;
using System.Collections.Generic;
using System.Linq;
using Symphony.Core;
using log4net;

namespace Symphony.ExternalDevices
{

    public sealed class AMSystemsDevice : ExternalDeviceBase
    {
        private static readonly ILog log = LogManager.GetLogger(typeof (AMSystemsDevice));

        private IDictionary<AMSystemsInterop.OperatingMode, IMeasurement> Backgrounds { get; set; }

        private IAMSystems AMSystems { get; set; }

        public const string SCALED_OUTPUT_STREAM_NAME = "SCALED_OUTPUT";
        public const string CAPACITANCE_TELEGRAPH_STREAM_NAME = "CAPACITANCE_TELEGRAPH";
        public const string FREQUENCY_TELEGRAPH_STREAM_NAME = "FREQUENCY_TELEGRAPH";
        public const string GAIN_TELEGRAPH_STREAM_NAME = "GAIN_TELEGRAPH";
        public const string MODE_TELEGRAPH_STREAM_NAME = "MODE_TELEGRAPH";
        public const string OVERLOAD_TELEGRAPH_STREAM_NAME = "OVERLOAD_TELEGRAPH";

        public AMSystemsDevice(IAMSystems amsystems, Controller c, IDictionary<AMSystemsInterop.OperatingMode, IMeasurement> background)
            : base("AMSystems", "AM Systems", c)
        {
            AMSystems = amsystems;

            c.Started += (sender, args) =>
                {
                    DeviceParameters = ReadDeviceParameters();
                };

            Backgrounds = background;
        }

        private readonly IDictionary<IDAQInputStream, IList<IInputData>> _queues = new Dictionary<IDAQInputStream, IList<IInputData>>();

        public override ExternalDeviceBase BindStream(string name, IDAQInputStream inputStream)
        {
            _queues.Add(inputStream, new List<IInputData>());
            return base.BindStream(name, inputStream);
        }

        public override void UnbindStream(string name)
        {
            if (Streams.ContainsKey(name) && Streams[name] is IDAQInputStream && _queues.ContainsKey((IDAQInputStream) Streams[name]))
            {
                _queues.Remove((IDAQInputStream)Streams[name]);
            }
            base.UnbindStream(name);
        }

        private static IDictionary<string, object> MergeDeviceParametersIntoConfiguration(
            IDictionary<string, object> config,
            AMSystemsInterop.AMSystemsData deviceParameters)
        {
            var result = config == null
                             ? new Dictionary<string, object>()
                             : new Dictionary<string, object>(config);

            result["Capacitance"] = deviceParameters.Capacitance;
            result["ExternalCommandSensitivity"] = deviceParameters.ExternalCommandSensitivity;
            result["ExternalCommandSensitivityUnits"] = deviceParameters.ExternalCommandSensitivityUnits.ToString();
            result["Frequency"] = deviceParameters.Frequency;
            result["Gain"] = deviceParameters.Gain;
            result["OperatingMode"] = deviceParameters.OperatingMode.ToString();
            result["Overload"] = deviceParameters.Overload;

            return result;
        }

        private AMSystemsInterop.AMSystemsData DeviceParameters { get; set; }

        public AMSystemsInterop.AMSystemsData CurrentDeviceParameters
        {
            get 
            { 
                var devParams = ReadDeviceParameters();
                DeviceParameters = devParams;
                return devParams;
            }
        }

        private AMSystemsInterop.AMSystemsData ReadDeviceParameters()
        {
            IDictionary<string, IInputData> data = new Dictionary<string, IInputData>();

            var inStreams = InputStreams;
            foreach (var stream in inStreams)
            {
                string name = Streams.FirstOrDefault(x => x.Value == stream).Key;
                data[name] = stream.Read();
            }

            return AMSystems.ReadTelegraphData(data);
        }

        public override IMeasurement Background
        {
            get
            {
                var bg = Backgrounds[CurrentDeviceParameters.OperatingMode];
                return bg;
            }
            set 
            { 
                Backgrounds[CurrentDeviceParameters.OperatingMode] = value;
            }
        }

        public static IMeasurement ConvertInput(IMeasurement sample, AMSystemsInterop.AMSystemsData deviceParams)
        {
            return MeasurementPool.GetMeasurement(
                sample.QuantityInBaseUnit/(decimal) deviceParams.Gain,
                InputUnitsExponentForMode(deviceParams.OperatingMode),
                InputUnitsForMode(deviceParams.OperatingMode));
        }

        private static int InputUnitsExponentForMode(AMSystemsInterop.OperatingMode mode)
        {
            switch (mode)
            {
                case AMSystemsInterop.OperatingMode.VTest:
                case AMSystemsInterop.OperatingMode.VComp:
                case AMSystemsInterop.OperatingMode.VClamp:
                    return -12; //pA
                case AMSystemsInterop.OperatingMode.IResist:
                case AMSystemsInterop.OperatingMode.IFollow:
                case AMSystemsInterop.OperatingMode.IClamp:
                case AMSystemsInterop.OperatingMode.I0:
                    return -3; //mV
                default:
                    throw new ArgumentOutOfRangeException("mode");
            }
        }

        private static string InputUnitsForMode(AMSystemsInterop.OperatingMode mode)
        {
            switch (mode)
            {
                case AMSystemsInterop.OperatingMode.VTest:
                case AMSystemsInterop.OperatingMode.VComp:
                case AMSystemsInterop.OperatingMode.VClamp:
                    return "A";
                case AMSystemsInterop.OperatingMode.IResist:
                case AMSystemsInterop.OperatingMode.IFollow:
                case AMSystemsInterop.OperatingMode.IClamp:
                case AMSystemsInterop.OperatingMode.I0:
                    return "V";
                default:
                    throw new ArgumentOutOfRangeException("mode");
            }
        }

        protected override IMeasurement ConvertOutput(IMeasurement deviceOutput)
        {
            return ConvertOutput(deviceOutput, CurrentDeviceParameters);
        }

        public static IMeasurement ConvertOutput(IMeasurement sample, AMSystemsInterop.AMSystemsData deviceParams)
        {
            switch (deviceParams.OperatingMode)
            {
                case AMSystemsInterop.OperatingMode.VTest:
                case AMSystemsInterop.OperatingMode.VComp:
                case AMSystemsInterop.OperatingMode.VClamp:
                    if (String.CompareOrdinal(sample.BaseUnit, "V") != 0)
                    {
                        throw new ArgumentException("Sample units must be in Volts.", "sample");
                    }

                    if (deviceParams.ExternalCommandSensitivityUnits != AMSystemsInterop.ExternalCommandSensitivityUnits.V_V)
                    {
                        throw new AMSystemsDeviceException("External command units are not V/V as expected for current device mode.");
                    }
                    break;
                case AMSystemsInterop.OperatingMode.IResist:
                case AMSystemsInterop.OperatingMode.IFollow:
                case AMSystemsInterop.OperatingMode.IClamp:
                case AMSystemsInterop.OperatingMode.I0:
                    if (String.CompareOrdinal(sample.BaseUnit, "A") != 0)
                    {
                        throw new ArgumentException("Sample units must be in Amps.", "sample");
                    }

                    if (deviceParams.ExternalCommandSensitivityUnits != AMSystemsInterop.ExternalCommandSensitivityUnits.A_V)
                    {
                        throw new AMSystemsDeviceException("External command units are not A/V as expected for current device mode.");
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            IMeasurement result;

            if (deviceParams.OperatingMode == AMSystemsInterop.OperatingMode.I0 || deviceParams.OperatingMode == AMSystemsInterop.OperatingMode.IFollow)
            {
                result = MeasurementPool.GetMeasurement(0, 0, "V");
            }
            else
            {
                result = (decimal)deviceParams.ExternalCommandSensitivity == 0
                             ? MeasurementPool.GetMeasurement(sample.Quantity, sample.Exponent, "V")
                             : MeasurementPool.GetMeasurement(sample.Quantity / (decimal)deviceParams.ExternalCommandSensitivity,
                                                  sample.Exponent, "V");
            }

            return result;
        }

        public override IOutputData PullOutputData(IDAQOutputStream stream, TimeSpan duration)
        {
            try
            {
                var deviceParameters = DeviceParameters;

                var config = MergeDeviceParametersIntoConfiguration(Configuration, deviceParameters);

                IOutputData data = this.Controller.PullOutputData(this, duration);

                return
                    data.DataWithConversion(m => ConvertOutput(m, deviceParameters))
                        .DataWithExternalDeviceConfiguration(this, config);
            }
            catch (Exception ex)
            {
                log.DebugFormat("Error pulling data from controller: {0} ({1})", ex.Message, ex);
                throw;
            }
        }

        public override void PushInputData(IDAQInputStream stream, IInputData inData)
        {
            _queues[stream].Add(inData);
            if (_queues.Values.Any(dataList => dataList.Count == 0))
            {
                return;
            }

            try
            {
                IDictionary<string, IInputData> data = new Dictionary<string, IInputData>();
                foreach (var dataList in _queues)
                {
                    string streamName = Streams.FirstOrDefault(x => x.Value == dataList.Key).Key;
                    data.Add(streamName, dataList.Value[0]);
                    dataList.Value.RemoveAt(0);
                }

                var deviceParameters = AMSystems.ReadTelegraphData(data);
                DeviceParameters = deviceParameters;

                IInputData scaledData = data[SCALED_OUTPUT_STREAM_NAME];
                IInputData convertedData = scaledData.DataWithConversion(m => ConvertInput(m, deviceParameters));

                var config = MergeDeviceParametersIntoConfiguration(Configuration, deviceParameters);

                this.Controller.PushInputData(this, convertedData.DataWithExternalDeviceConfiguration(this, config));
            }
            catch (Exception ex)
            {
                log.DebugFormat("Error pushing data to controller: {0} ({1})", ex.Message, ex);
                throw;
            }
        }

    }

    public class AMSystemsDeviceException : ExternalDeviceException
    {
        public AMSystemsDeviceException(string message)
            : base(message)
        {
        }
    }
}
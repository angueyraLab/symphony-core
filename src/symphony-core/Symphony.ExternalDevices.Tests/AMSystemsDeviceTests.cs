// src/symphony-core/Symphony.ExternalDevices.Tests/AMSystemsDeviceTests.cs

using System;
using System.Collections.Generic;
using System.Linq;
using Symphony.Core;

namespace Symphony.ExternalDevices
{
    using NUnit.Framework;

    [TestFixture]
    class AMSystems2400Tests
    {
        [Test]
        public void ShouldReadSimpleTelegraph()
        {
            IAMSystems patch = new AMSystems2400();

            IDictionary<string, IInputData> data = new Dictionary<string, IInputData>();

            data[AMSystemsDevice.GAIN_TELEGRAPH_STREAM_NAME] = new InputData(Enumerable.Repeat(new Measurement(1.9, "V"), 10), null, DateTimeOffset.Now);
            data[AMSystemsDevice.MODE_TELEGRAPH_STREAM_NAME] = new InputData(Enumerable.Repeat(new Measurement(6.1, "V"), 10), null, DateTimeOffset.Now);

            AMSystemsInterop.AMSystemsData telegraph = patch.ReadTelegraphData(data);
            Assert.That(telegraph.Gain, Is.EqualTo(0.5));
            Assert.That(telegraph.OperatingMode, Is.EqualTo(AMSystemsInterop.OperatingMode.VClamp));
            Assert.That(telegraph.ExternalCommandSensitivity, Is.EqualTo(0.02));
            Assert.That(telegraph.ExternalCommandSensitivityUnits, Is.EqualTo(AMSystemsInterop.ExternalCommandSensitivityUnits.V_V));
        }

    }

    [TestFixture]
    class AMSystemsDeviceTests
    {
        [Test]
        public void ShouldConvertOutputUnitsInIClamp(
            [Values(
                AMSystemsInterop.OperatingMode.I0, 
                AMSystemsInterop.OperatingMode.IClampFast, 
                AMSystemsInterop.OperatingMode.IClampNormal)] AMSystemsInterop.OperatingMode operatingMode)
        {
            var c = new Controller();
            var p = new AMSystems2400();

            var patchDevice = new AMSystemsDevice(p, c, null);

            var data = new AMSystemsInterop.AMSystemsData()
                {
                    OperatingMode = operatingMode,
                    ExternalCommandSensitivity = 2.5,
                    ExternalCommandSensitivityUnits = AMSystemsInterop.ExternalCommandSensitivityUnits.A_V
                };

            var cmd = new Measurement(20, -12, "A");

            var expected = operatingMode == AMSystemsInterop.OperatingMode.I0 ?
                new Measurement(0, "V") :
                new Measurement(cmd.Quantity / (decimal)data.ExternalCommandSensitivity,
                                           cmd.Exponent, "V");

            var actual = AMSystemsDevice.ConvertOutput(cmd, data);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void ShouldConvertOutputUnitsInVClamp(
            [Values(
                AMSystemsInterop.OperatingMode.VClamp,
                AMSystemsInterop.OperatingMode.Track)] AMSystemsInterop.OperatingMode operatingMode)
        {
            var data = new AMSystemsInterop.AMSystemsData()
            {
                OperatingMode = operatingMode,
                ExternalCommandSensitivity = 2.5,
                ExternalCommandSensitivityUnits = AMSystemsInterop.ExternalCommandSensitivityUnits.V_V
            };

            var cmd = new Measurement(20, -3, "V");

            var expected = operatingMode == AMSystemsInterop.OperatingMode.Track ?
                new Measurement(0, "V") :
                new Measurement(cmd.Quantity / (decimal)data.ExternalCommandSensitivity,
                                           cmd.Exponent, "V");

            var actual = AMSystemsDevice.ConvertOutput(cmd, data);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void ShouldConvertInputUnitsInVClamp(
            [Values(
                AMSystemsInterop.OperatingMode.VClamp, 
                AMSystemsInterop.OperatingMode.Track)] AMSystemsInterop.OperatingMode operatingMode,
            [Values(1, 2, 10)] double gain)
        {
            var data = new AMSystemsInterop.AMSystemsData()
                {
                    OperatingMode = operatingMode,
                    Gain = gain
                };

            var cmd = new Measurement(20, -3, "V");

            var expected = new Measurement(cmd.QuantityInBaseUnit/(decimal) gain, -12, "A");

            var actual = AMSystemsDevice.ConvertInput(cmd, data);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void ShouldConvertInputUnitsInIClamp(
            [Values(
                AMSystemsInterop.OperatingMode.I0, 
                AMSystemsInterop.OperatingMode.IClampFast,
                AMSystemsInterop.OperatingMode.IClampNormal)] AMSystemsInterop.OperatingMode operatingMode,
            [Values(1, 2, 10)] double gain)
        {
            var data = new AMSystemsInterop.AMSystemsData()
                {
                    OperatingMode = operatingMode,
                    Gain = gain
                };

            var cmd = new Measurement(20, -3, "V");

            var expected = new Measurement(cmd.QuantityInBaseUnit/(decimal) gain, -3, "V");

            var actual = AMSystemsDevice.ConvertInput(cmd, data);

            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void ShouldUseBackgroundForMode()
        {
            const string VClampUnits = "V";
            const string IClampUnits = "A";

            Measurement VClampBackground = new Measurement(2, -3, VClampUnits);
            Measurement IClampBackground = new Measurement(-10, -3, IClampUnits);

            var c = new Controller();
            var p = new FakeAMSystems();

            var bg = new Dictionary<AMSystemsInterop.OperatingMode, IMeasurement>()
                         {
                             {AMSystemsInterop.OperatingMode.VClamp, VClampBackground},
                             {AMSystemsInterop.OperatingMode.IClampNormal, IClampBackground},  
                         };

            var patch = new AMSystemsDevice(p, c, bg);
            patch.BindStream(new DAQOutputStream("stream"));

            var data = new AMSystemsInterop.AMSystemsData()
            {
                OperatingMode = AMSystemsInterop.OperatingMode.VClamp,
                ExternalCommandSensitivity = 2.5,
                ExternalCommandSensitivityUnits = AMSystemsInterop.ExternalCommandSensitivityUnits.V_V
            };

            p.Data = data;

            Assert.That(patch.OutputBackground, Is.EqualTo(AMSystemsDevice.ConvertOutput(VClampBackground, patch.CurrentDeviceParameters)));

            data = new AMSystemsInterop.AMSystemsData()
            {
                OperatingMode = AMSystemsInterop.OperatingMode.IClampNormal,
                ExternalCommandSensitivity = 1.5,
                ExternalCommandSensitivityUnits = AMSystemsInterop.ExternalCommandSensitivityUnits.A_V
            };

            p.Data = data;

            Assert.That(patch.OutputBackground, Is.EqualTo(AMSystemsDevice.ConvertOutput(IClampBackground, patch.CurrentDeviceParameters)));
        }
    }

    internal class FakeAMSystems : IAMSystems
    {
        public AMSystemsInterop.AMSystemsData Data { get; set; }

        public AMSystemsInterop.AMSystemsData ReadTelegraphData(IDictionary<string, IInputData> data)
        {
            return Data;
        }
    }
}

﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Concurrent;
using log4net;

namespace Symphony.Core
{
    /// <summary>
    /// Serves as the "core of the Core": a clearinghouse for epoch
    /// descriptions, which are made available for IExternalDevice
    /// instances to pull "outgoing" stimulus commands, and capture
    /// the IExternalDevice's "incoming" responses to those stimulus
    /// commands. The Controller manages the bookkeeping of providing
    /// appropriate stimuli for each device and assigning incoming responses
    /// to the appropriate Epoch's Response.
    /// 
    /// <para>The terms "outgoing" and "incoming" can be confusing;
    /// these are from the perspective of the PC driving the experiment
    /// rig. Commands are "outgoing" from the PC to the sensors and
    /// apparatus, and the data generated by the hardware rig are "incoming" 
    /// to the PC.</para>
    /// 
    /// <para>In truth, "outgoing" doesn't imply that the Controller
    /// "pushes" the commands out to the device--the device will pull them
    /// from the Controller, so the Controller only needs to maintain a
    /// stream of them for the device to pull when ready. And, to complete
    /// the role-reversal, the device "pushes" the results to the Controller
    /// when the results are coming back from the device.</para>
    /// 
    /// </summary>
    public class Controller : ITimelineProducer
    {
        private readonly object _eventLock = new Object();

        /// <summary>
        /// Construct a Controller object and input/ouput pipeline from the configuration file at
        /// the given path.
        /// </summary>
        /// <param name="configurationPath">Configuration file path</param>
        /// <returns>Configured Controller and input/output pipelines</returns>
        public static Controller FromConfiguration(string configurationPath)
        {
            return new Parser().ParseConfiguration(configurationPath);
        }

        /// <summary>
        /// Construct a new Controller with no DAQ bridge and using the system (CPU) clock.
        /// </summary>
        public Controller() : this(null, new SystemClock())
        {
        }

        /// <summary>
        /// Construct a Controller with Clock and DAQ Bridge.
        /// </summary>
        /// <param name="daq">DAQ bridge to use as the pipeline endpoint</param>
        /// <param name="clock">Canonical clock</param>
        public Controller(IDAQController daq, IClock clock)
        {
            Clock = clock;
            DAQController = daq;

            Init();
        }

        private void Init()
        {
            Devices = new HashSet<IExternalDevice>();
            EpochQueue = new ConcurrentQueue<Epoch>();
            Configuration = new Dictionary<string, object>();
            UnusedInputData = new ConcurrentDictionary<ExternalDeviceBase, InputDataPair>();
            SerialTaskScheduler = new LimitedConcurrencyLevelTaskScheduler(1);
        }

        private TaskScheduler SerialTaskScheduler { get; set; }

        /// <summary>
        /// The canonical clock for the experimental timeline
        /// </summary>
        public IClock Clock { get; set; }

        /// <summary>
        /// Parameters of this controller's configuration.
        /// </summary>
        public IDictionary<string, object> Configuration { get; private set; }

        /// <summary>
        /// A Controller has 0..n Devices connected to it. It is callers'
        /// responsibility to ensure the ExternalDevice refers back to this
        /// Controller. (Use the Validate() method to ensure all the
        /// connections are wired up correctly.). You should use
        /// AddDevice to add a device to this controller rather than manipulating
        /// Devices directly.
        /// </summary>
        /// 
        /// <see cref="AddDevice"/>
        public ISet<IExternalDevice> Devices { get; private set; }

        /// <summary>
        /// The DAQ hardware controller for this Controller's configuration.
        /// </summary>
        public IDAQController DAQController { get; set; }

        /// <summary>
        /// Enumerable collection of available IHardwareControllers in this Controller's
        /// input/ouput piplines. IHardwareControllers may be DAQ Controllers, video output
        /// controllers, etc.
        /// </summary>
        public IEnumerable<IHardwareController> HardwareControllers
        {
            get
            {
                return new IHardwareController[] { DAQController };
            }
        }

        /// <summary>
        /// Add an ExternalDevice to the Controller; take care of performing
        /// whatever wiring up between the Controller and the ExternalDevice
        /// needs to be done, as well.
        /// </summary>
        /// <param name="dev">The ExternalDevice to wire up</param>
        /// <exception cref="InvalidOperationException">This controller already has a device with the same name.</exception>
        /// <returns>This instance, for fluent-style API calls</returns>
        public Controller AddDevice(IExternalDevice dev)
        {
            if (Devices.Where(d => d.Name == dev.Name).Any())
                throw new InvalidOperationException("Device with name " + dev.Name + " already exists.");

            Devices.Add(dev);
            dev.Controller = this;
            return this;
        }

        /// <summary>
        /// Gets the device with given name connected to this controller. Because devices must be unique
        /// by name, there can be at most one device with the given name.
        /// </summary>
        /// <param name="name">Device name</param>
        /// <returns>ExternalDevice instance with the given name or null if none exists</returns>
        /// <see cref="AddDevice"/>
        public IExternalDevice GetDevice(string name)
        {
            return Devices.Where(d => d.Name == name).DefaultIfEmpty(null).First();
        }

        /// <summary>
        /// Double-check that all the pieces of the pipeline are wired up
        /// correctly--the ExternalDevices all point to this controller,
        /// and so on. Recursively validates on down the line, so any
        /// ExternalDevices connected here will have their Validate() method
        /// called as part of this, and so on.
        /// </summary>
        /// <returns>A monad indicating validation (as a bool) or the error message (if cast to a string)</returns>
        public Maybe<string> Validate()
        {
            foreach (ExternalDeviceBase ed in Devices)
            {
                if (ed.Controller != this)
                {
                    // We can either bail out (return false), or fix it
                    // I think we're OK to just fix it
                    ed.Controller = this;
                }

                Maybe<string> edVal = ed.Validate();
                if (edVal != true)
                {
                    // Beginning to think there's a better way than just
                    // returning false--if we fail, we probably should have
                    // some kind of idea of what failed
                    return Maybe<string>.No("External device " +
                        ed.ToString() + " failed to validate: " +
                        edVal.Item2);
                }
            }

            if (Clock == null)
                return Maybe<string>.No("Controller.Clock must not be null.");

            if (DAQController == null)
                return Maybe<string>.No("Controller.DAQController must not be null.");

            return Maybe<string>.Yes();
        }


        /// <summary>
        /// Pulls IOutputData from the current Epoch destined for a given external deivce.
        /// Result will have duration greater than zero, but may not equal the requested duration.
        /// </summary>
        /// <param name="device">ExternalDevice for this outputdata</param>
        /// <param name="duration">Duration of the data requested</param>
        /// <returns>Output data for the requested device</returns>
        public virtual IOutputData PullOutputData(IExternalDevice device, TimeSpan duration)
        {
            if (CurrentEpoch == null)
                return null;

            return CurrentEpoch.PullOutputData(device, duration);
        }

        /// <summary>
        /// This controller received input data.
        /// </summary>
        public event EventHandler<TimeStampedDeviceDataEventArgs> ReceivedInputData;

        /// <summary>
        /// This controller pushed input data to an Epoch.
        /// </summary>
        public event EventHandler<TimeStampedEpochEventArgs> PushedInputData;

        /// <summary>
        /// This controller persisted a completed Epoch.
        /// </summary>
        public event EventHandler<TimeStampedEpochEventArgs> SavedEpoch;

        /// <summary>
        /// This controller completed an Epoch
        /// </summary>
        public event EventHandler<TimeStampedEpochEventArgs> CompletedEpoch;

        /// <summary>
        /// This controller discarded a running epoch due to an exception in the output or input pipelines.
        /// </summary>
        public event EventHandler<TimeStampedEpochEventArgs> DiscardedEpoch;

        /// <summary>
        /// This controller received a NextEpoch() request.
        /// </summary>
        public event EventHandler<TimeStampedEventArgs> NextEpochRequested;

        private void OnReceivedInputData(IExternalDevice device, IIOData data)
        {
            FireEvent(ReceivedInputData, device, data);
        }

        private void OnPushedInputData(Epoch epoch)
        {
            FireEvent(PushedInputData, epoch);
        }

        private void OnSavedEpoch(Epoch epoch)
        {
            FireEvent(SavedEpoch, epoch);
        }

        private void OnCompletedEpoch(Epoch epoch)
        {
            FireEvent(CompletedEpoch, epoch);
        }

        private void OnDiscardedEpoch(Epoch epoch)
        {
            FireEvent(DiscardedEpoch, epoch);
        }

        private void OnNextEpochRequested()
        {
            FireEvent(NextEpochRequested);
        }

        private void FireEvent(EventHandler<TimeStampedEpochEventArgs> evt, Epoch epoch)
        {
            FireEvent(evt, new TimeStampedEpochEventArgs(Clock, epoch));
        }

        private void FireEvent(EventHandler<TimeStampedDeviceDataEventArgs> evt, IExternalDevice device, IIOData data)
        {
            FireEvent(evt, new TimeStampedDeviceDataEventArgs(Clock, device, data));
        }

        private void FireEvent(EventHandler<TimeStampedEventArgs> evt)
        {
            FireEvent(evt, new TimeStampedEventArgs(Clock));
        }

        private void FireEvent<T>(EventHandler<T> evt, T args) where T : TimeStampedEventArgs
        {
            lock (_eventLock)
            {
                if (evt != null)
                {
                    evt(this, args);
                }
            }
        }

        /// <summary>
        /// Null Fragment indidcates no fragment.
        /// </summary>
        private class InputDataPair : Tuple<IInputData, Queue<IInputData>>
        {
            public InputDataPair()
                : this(null, new Queue<IInputData>())
            {
            }

            public InputDataPair(IInputData item1, Queue<IInputData> item2)
                : base(item1, item2)
            {
            }

            public IInputData Fragment { get { return this.Item1; } }

            public Queue<IInputData> Queue { get { return this.Item2; } }
        }

        /// <summary>
        /// Transactional usage must be locked
        /// </summary>
        private ConcurrentDictionary<ExternalDeviceBase, InputDataPair> UnusedInputData { get; set; }

        /// <summary>
        /// Push IInputData from a given ExternalDevice to the appropriate Response of the current
        /// Epoch.
        /// </summary>
        /// <param name="device">ExternalDevice providing the data</param>
        /// <param name="inData">Input data instsance</param>
        public virtual void PushInputData(ExternalDeviceBase device, IInputData inData)
        {
            //TODO update this to let Epoch use _completionLock around Response duration and appending

            try
            {
                OnReceivedInputData(device, inData);
            }
            catch (Exception e)
            {
                log.ErrorFormat("Unable to notify observers of incoming data: {0}", e);
            }
            
            var currentEpoch = CurrentEpoch;

            if (currentEpoch != null &&
                currentEpoch.Responses.ContainsKey(device))
            {
                if (!UnusedInputData.ContainsKey(device))
                    UnusedInputData[device] = new InputDataPair();

                lock (UnusedInputData[device])
                {
                    UnusedInputData[device].Queue.Enqueue(inData);


                    if (UnusedInputData[device].Fragment != null) //null indicates no fragment present
                    {
                        var fragment = UnusedInputData[device].Fragment;
                        var splitFragment =
                            fragment.SplitData(currentEpoch.Duration - currentEpoch.Responses[device].Duration);

                        currentEpoch.Responses[device].AppendData(splitFragment.Head);

                        if (splitFragment.Rest.Duration > TimeSpan.Zero)
                        {
                            UnusedInputData[device] = new InputDataPair(splitFragment.Rest,
                                                                        UnusedInputData[device].Queue);
                        }
                        else
                        {
                            UnusedInputData[device] = new InputDataPair(null,
                                                                        UnusedInputData[device].Queue);
                        }
                    }

                    while (UnusedInputData[device].Queue.Any() &&
                           currentEpoch.Responses[device].Duration < currentEpoch.Duration)
                    {
                        if (UnusedInputData[device].Fragment != null)
                        {
                            throw new SymphonyControllerException("Input data fragment should be empty");
                        }

                        var cData = UnusedInputData[device].Queue.Dequeue();

                        var splitData =
                            cData.SplitData(currentEpoch.Duration - currentEpoch.Responses[device].Duration);

                        currentEpoch.Responses[device].AppendData(splitData.Head);
                        if (splitData.Rest.Duration > TimeSpan.Zero)
                        {
                            UnusedInputData[device] = new InputDataPair(splitData.Rest,
                                                                        UnusedInputData[device].Queue);
                        }
                    }


                    try
                    {
                        OnPushedInputData(currentEpoch);
                    }
                    catch (Exception e)
                    {
                        log.ErrorFormat("Unable to notify observers of pushed input data: {0}", e);
                    }
                }
            }

        }


        /// <summary>
        /// The Epoch instance currently being fed through the rig or null if there is
        /// no current Epoch.
        /// </summary>
        public Epoch CurrentEpoch { get; private set; }


        protected ConcurrentQueue<Epoch> EpochQueue { get; private set; }

        /// <summary>
        /// Add an Epoch to the Controller's Epoch queue. Epochs are presented in FIFO order from
        /// this queue when running. You can use RunEpoch to bypass this queue, presenting an Epoch
        /// immediately
        /// </summary>
        /// <param name="e">Epoch to add to the queue</param>
        /// <see cref="RunEpoch"/>
        public void EnqueueEpoch(Epoch e)
        {
            ValidateEpoch(e);
            EpochQueue.Enqueue(e);
        }

        private static bool ValidateEpoch(Epoch epoch)
        {
            if (epoch.IsIndefinite && epoch.Responses.Count > 0)
                return false;

            if (epoch.Stimuli.Values.Any(s => ((bool)s.Duration) != ((bool)epoch.Duration) || ((TimeSpan)s.Duration).Ticks != ((TimeSpan)epoch.Duration).Ticks))
                return false;

            return true;
        }


        /// <summary>
        /// Begin a new Epoch Group (i.e. a logical block of Epochs). As each Epoch Group is persisted
        /// to a separate data file, this method creates the appropriate output file and
        /// EpochPersistor instance.
        /// </summary>
        /// <param name="path">The name of the file into which to store the epoch; if the name
        ///   ends in ".xml", it will store the file using the EpochXMLPersistor, and if the name
        ///   ends in ".hdf5", it will store the file using the EpochHDF5Persistor. This file will
        ///   be overwritten if it already exists at this location.</param>
        /// <param name="epochGroupLabel">Label for the new Epoch Group</param>
        /// <param name="source">Identifier for EpochGroup's Source</param>
        /// <param name="keywords"></param>
        /// <param name="properties"></param>
        /// <returns>The EpochPersistor instance to be used for saving Epochs</returns>
        /// <see cref="RunEpoch"/>
        public EpochPersistor BeginEpochGroup(string path, string epochGroupLabel, string source, IEnumerable<string> keywords, IDictionary<string, object> properties)
        {
            EpochPersistor result = null;
            if (path.EndsWith(".xml"))
            {
                result = new EpochXMLPersistor(path);
            }
            else if (path.EndsWith(".hdf5"))
            {
                result = new EpochHDF5Persistor(path, null);
            }
            else
                throw new ArgumentException(String.Format("{0} doesn't look like a legit Epoch filename", path));

            var kws = keywords == null ? new string[0] : keywords.ToArray();
            var props = properties ?? new Dictionary<string, object>();

            result.BeginEpochGroup(epochGroupLabel, source, kws, props, Guid.NewGuid(), DateTime.Now);

            return result;
        }

        /// <summary>
        /// Closes an Epoch Group. This method should be called after running all Epochs in the
        /// Epoch Group represented by EpochPersistor to give the persistor a chance to write any
        /// neceesary closing information.
        /// <para>
        /// Closes the persistor's file.
        /// </para>
        /// </summary>
        /// <param name="e">EpochPersistor representing the completed EpochGroup</param>
        /// <see cref="BeginEpochGroup"/>
        public void EndEpochGroup(EpochPersistor e)
        {
            e.EndEpochGroup();
            e.Close();
        }


        /// <summary>
        /// Matlab-friendly factory method to run a single Epoch.
        /// </summary>
        /// <remarks>Constructs an Epoch with homogenous stimulus ID, sample rate and units, then runs the
        /// constructed Epoch.
        /// </remarks>
        /// 
        /// <param name="protocolID">Protocol ID of the constructed Epoch</param>
        /// <param name="parameters">Protocol parameters of the constructed Epoch</param>
        /// <param name="stimulusID">Stimulus plugin ID for all constructed stimuli</param>
        /// <param name="stimulusSampleRate">Simulus sample rate for all constructed stimuli</param>
        /// <param name="stimuli">Simulus data for output devices</param>
        /// <param name="backgrounds">Backgrounds for output devices</param>
        /// <param name="responses">Devices from which to record Responses</param>
        /// <param name="persistor">EpochPersistor to persist Epoch</param>
        public void RunEpoch(
            string protocolID,
            IDictionary<string, object> parameters,
            string stimulusID,
            Measurement stimulusSampleRate,
            IDictionary<ExternalDeviceBase, IEnumerable<IMeasurement>> stimuli,
            IDictionary<ExternalDeviceBase, IMeasurement> backgrounds,
            IEnumerable<ExternalDeviceBase> responses,
            EpochPersistor persistor)
        {
            var epoch = new Epoch(protocolID,
                              parameters);
            foreach (var dev in stimuli.Keys)
            {
                var data = new OutputData(stimuli[dev],
                                          stimulusSampleRate,
                                          true);
                var stim = new RenderedStimulus(stimulusID,
                    (IDictionary<string, object>) new Dictionary<string, object> { { "data", data } },
                    (IOutputData) data);

                epoch.Stimuli[dev] = stim;
            }

            foreach (var dev in responses)
            {
                epoch.Responses[dev] = new Response();
            }

            foreach (var dev in backgrounds.Keys)
            {
                epoch.Background[dev] = new Epoch.EpochBackground(backgrounds[dev], stimulusSampleRate);
            }

            RunEpoch(epoch, persistor);
        }

        /// <summary>
        /// The core entry point for the Controller Facade; push an Epoch in here, and when the
        /// Epoch is finished processing, control will be returned to you. 
        /// 
        /// <para>In other words, this
        /// method is blocking--the Controller cannot run more than one Epoch at a time.</para>
        /// </summary>
        /// 
        /// <param name="e">Single Epoch to present</param>
        /// <param name="persistor">EpochPersistor for saving the data. May be null to indicate epoch should not be persisted</param>
        /// <exception cref="ValidationException">Validation failed for this Controller</exception>
        public void RunEpoch(Epoch e, EpochPersistor persistor)
        {
            var cts = new CancellationTokenSource();
            var cancellationToken = cts.Token;

            Task persistenceTask = null;

            if (!ValidateEpoch(e))
                throw new ArgumentException("Epoch is not valid");


            if (!Validate())
                throw new ValidationException(Validate());


            // Starting with this Epoch

            var cEpoch = CurrentEpoch;
            CurrentEpoch = e;

            EventHandler<TimeStampedEventArgs> nextRequested = (c, args) =>
            {
                DAQController.RequestStop();
                OnDiscardedEpoch(CurrentEpoch);
            };

            bool epochPersisted = false;
            EventHandler<TimeStampedEpochEventArgs> inputPushed = (c, args) =>
            {
                if (CurrentEpoch != null &&
                    CurrentEpoch.IsComplete)
                {
                    log.Debug("Epoch complete. Requesting DAQController stop.");
                    DAQController.RequestStop();
                    if (persistor != null && !epochPersisted)
                    {
                        Epoch completedEpoch = CurrentEpoch;
                        persistenceTask = Task.Factory.StartNew(() =>
                                                                    {
                                                                        log.DebugFormat("Saving completed Epoch ({0})...", completedEpoch.StartTime);
                                                                        SaveEpoch(persistor, completedEpoch);
                                                                    },
                            cancellationToken,
                            TaskCreationOptions.PreferFairness,
                            SerialTaskScheduler)
                            .ContinueWith((task) =>
                                              {
                                                  cancellationToken.ThrowIfCancellationRequested();

                                                  if (task.IsFaulted &&
                                                      task.Exception != null)
                                                  {
                                                      throw task.Exception;
                                                  }

                                                  OnCompletedEpoch(completedEpoch);
                                              },
                                              cancellationToken);

                        epochPersisted = true;
                    }
                }
            };

            EventHandler<TimeStampedExceptionEventArgs> exceptionalStop = (daq, args) =>
                                                                              {
                                                                                  log.Debug(
                                                                                      "Discarding epoch due to exception");
                                                                                  OnDiscardedEpoch(CurrentEpoch);
                                                                                  throw new SymphonyControllerException(
                                                                                      "DAQ Controller stopped", args.Exception);
                                                                              };

            try
            {
                NextEpochRequested += nextRequested;
                PushedInputData += inputPushed;
                DAQController.ExceptionalStop += exceptionalStop;

                e.StartTime = Maybe<DateTimeOffset>.Some(this.Clock.Now);

                log.DebugFormat("Starting epoch: {0}", CurrentEpoch.ProtocolID);
                DAQController.Start(false);
            }
            finally
            {
                CurrentEpoch = cEpoch;
                NextEpochRequested -= nextRequested;
                PushedInputData -= inputPushed;
                DAQController.ExceptionalStop -= exceptionalStop;

                DAQController.WaitForInputTasks();

                //Clear remaining input
                UnusedInputData.Clear();
            }

            if (persistenceTask != null)
            {
                try
                {
                    persistenceTask.Wait();
                }
                catch (AggregateException ex)
                {
                    log.ErrorFormat("An error occurred while saving Epoch: {0}", ex);
                    throw new SymphonyControllerException("Unable to write Epoch data to persistor.",
                        ex.Flatten());
                }
            }
        }

        private static readonly ILog log = LogManager.GetLogger(typeof(Controller));

        private void SaveEpoch(EpochPersistor persistor, Epoch e)
        {
            persistor.Serialize(e);
            OnSavedEpoch(e);
        }

        /// <summary>
        /// Request that the Controller abort the current Epoch and move on to the next Epoch in
        /// the EpochQueue. If no next Epoch is available (or if the Controller running a single Epoch via
        /// RunEpoch), this method will stop the input/output pipelines.
        /// </summary>
        public void NextEpoch()
        {
            SkipCurrentEpoch(true);
        }

        private void SkipCurrentEpoch(bool moveNext)
        {
            if (moveNext)
            {
                Epoch epoch;
                if (EpochQueue.TryDequeue(out epoch))
                {
                    CurrentEpoch = epoch;
                }
                else
                {
                    throw new SymphonyControllerException("Cannot dequeue next epoch from Controller queue.");
                }
            }
            else
            {
                CurrentEpoch = null;
            }

            OnNextEpochRequested();
        }

        /// <summary>
        /// Requests that the Controller abort the current Epoch and stop the input/output pipelines.
        /// </summary>
        public void CancelEpoch()
        {
            SkipCurrentEpoch(false);
        }


        /// <summary>
        /// Inform this Controller that the output pipeline send output data "to the wire".
        /// </summary>
        /// <param name="device">ExternalDevice that output the data</param>
        /// <param name="outputTime">Approximate time the data was sent to the wire</param>
        /// <param name="duration">Duration of the data block send to the wire</param>
        /// <param name="configuration">Pipeline node configuration(s) for the output pipeline that processed the outgoing data</param>
        public virtual void DidOutputData(IExternalDevice device, DateTimeOffset outputTime, TimeSpan duration, IEnumerable<IPipelineNodeConfiguration> configuration)
        {
            //virtual for Moq
            if (CurrentEpoch != null && !CurrentEpoch.IsComplete)
            {
                CurrentEpoch.DidOutputData(device, outputTime, duration, configuration);
            }
        }
    }

    /// <summary>
    /// Exception indicating a Symphony.Core.Controller excpetion.
    /// </summary>
    public class SymphonyControllerException : SymphonyException
    {
        public SymphonyControllerException(string message)
            : base(message)
        {
        }

        public SymphonyControllerException(string message, Exception underlyingException)
            : base(message, underlyingException)
        {
        }
    }

}

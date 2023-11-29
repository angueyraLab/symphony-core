// src/symphony-core/Symphony.ExternalDevices/IAxopatch.cs
using System.Collections.Generic;
using Symphony.Core;

namespace Symphony.ExternalDevices
{
    public interface IAMSystems
    {
        /// <summary>
        /// Interprets the given telegraph reading
        /// </summary>
        AMSystemsInterop.AMSystemsData ReadTelegraphData(IDictionary<string, IInputData> data);
    }
}
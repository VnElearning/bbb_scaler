﻿using HPI.BBB.Autoscaler.APIs;
using HPI.BBB.Autoscaler.Models;
using HPI.BBB.Autoscaler.Models.Ionos;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HPI.BBB.Autoscaler.Utils
{
    public class ScalingHelper
    {

        private static readonly int MINIMUM_ACTIVE_MACHINES = int.Parse(ConfigReader.GetValue("MINIMUM_ACTIVE_MACHINES", "DEFAULT", "MINIMUM_ACTIVE_MACHINES"), CultureInfo.InvariantCulture);
        private static readonly float MAX_ALLOWED_MEMORY_WORKLOAD = float.Parse(ConfigReader.GetValue("MAX_ALLOWED_MEMORY_WORKLOAD", "DEFAULT", "MAX_ALLOWED_MEMORY_WORKLOAD"), CultureInfo.InvariantCulture);
        private static readonly float MAX_ALLOWED_CPU_WORKLOAD = float.Parse(ConfigReader.GetValue("MAX_ALLOWED_CPU_WORKLOAD", "DEFAULT", "MAX_ALLOWED_CPU_WORKLOAD"), CultureInfo.InvariantCulture);
        private static readonly float MIN_ALLOWED_MEMORY_WORKLOAD = float.Parse(ConfigReader.GetValue("MIN_ALLOWED_MEMORY_WORKLOAD", "DEFAULT", "MIN_ALLOWED_MEMORY_WORKLOAD"), CultureInfo.InvariantCulture);
        private static readonly float MIN_ALLOWED_CPU_WORKLOAD = float.Parse(ConfigReader.GetValue("MIN_ALLOWED_CPU_WORKLOAD", "DEFAULT", "MIN_ALLOWED_CPU_WORKLOAD"), CultureInfo.InvariantCulture);
        private static readonly int MAX_WORKER_MEMORY = int.Parse(ConfigReader.GetValue("MAX_WORKER_MEMORY", "DEFAULT", "MAX_WORKER_MEMORY"), CultureInfo.InvariantCulture);
        private static readonly int DEFAULT_WORKER_MEMORY = int.Parse(ConfigReader.GetValue("DEFAULT_WORKER_MEMORY", "DEFAULT", "DEFAULT_WORKER_MEMORY"), CultureInfo.InvariantCulture);
        private static readonly int MAX_WORKER_CPU = int.Parse(ConfigReader.GetValue("MAX_WORKER_CPU", "DEFAULT", "MAX_WORKER_CPU"), CultureInfo.InvariantCulture);
        private static readonly int DEFAULT_WORKER_CPU = int.Parse(ConfigReader.GetValue("DEFAULT_WORKER_CPU", "DEFAULT", "DEFAULT_WORKER_CPU"), CultureInfo.InvariantCulture);

        internal static void ShutDown(ILogger log, List<WorkloadMachineTuple> totalWorkload, BBBAPI bbb, IonosAPI ionos, string ionosDataCenter)
        {
            var shutDown = totalWorkload
                        .OrderByDescending(m => m.Workload.MemoryUtilization)
                        .ThenByDescending(m => m.Workload.CPUUtilization)
                        .Skip(MINIMUM_ACTIVE_MACHINES)
                        .Select(async m => new MachineWorkloadStatsTuple(m.Machine, m.Workload, await bbb.GetMeetingsAsync(m.Machine.PrimaryIP).ConfigureAwait(false)))
                        .Select(res => res.Result)
                        .Where(m => (m.Workload.MemoryUtilization < MIN_ALLOWED_MEMORY_WORKLOAD ||
                        m.Workload.CPUUtilization < MIN_ALLOWED_CPU_WORKLOAD) && m.Machine.Properties.Cores <= DEFAULT_WORKER_CPU &&
                        m.Machine.Properties.Ram <= DEFAULT_WORKER_MEMORY && m.Stats.Sum(u => u.ParticipantCount) == 0).ToList();

            log.LogInformation($"Found '{shutDown.Count}' machines to shut down");
            shutDown.AsParallel().ForAll(async m => await ionos.TurnMachineOff(m.Machine.Id, ionosDataCenter).ConfigureAwait(false));
        }

        internal static void ScaleMemoryDown(ILogger log, List<WorkloadMachineTuple> totalWorkload, IonosAPI ionos, string ionosDataCenter)
        {
            var scaleMemoryDown = totalWorkload.OrderByDescending(m => m.Workload.MemoryUtilization).Where(m => m.Workload.MemoryUtilization < MIN_ALLOWED_MEMORY_WORKLOAD
                               && m.Machine.Properties.Ram > DEFAULT_WORKER_MEMORY).ToList();
            log.LogInformation($"Found '{scaleMemoryDown.Count}' machines to scale down");
            scaleMemoryDown.AsParallel().ForAll(async m =>
            {
                var machine = m.Machine;
                log.LogInformation($"Scale memory of machine '{machine.PrimaryIP}' down");
                var update = new IonosMachineUpdate { Ram = machine.Properties.Ram - 1024 };
                await ionos.UpdateMachines(machine.Id, ionosDataCenter, update).ConfigureAwait(false);
            });
        }

        internal static void ScaleCPUDown(ILogger log, List<WorkloadMachineTuple> totalWorkload, IonosAPI ionos, string ionosDataCenter)
        {
            var scaleCpuDown = totalWorkload.OrderByDescending(m => m.Workload.CPUUtilization).Where(m => m.Workload.CPUUtilization < MIN_ALLOWED_CPU_WORKLOAD
                               && m.Machine.Properties.Cores > DEFAULT_WORKER_CPU).ToList();
            log.LogInformation($"Found '{scaleCpuDown.Count}' machines to scale down");
            scaleCpuDown.AsParallel().ForAll(async m =>
            {
                var machine = m.Machine;
                log.LogInformation($"Scale machine '{machine.PrimaryIP}' down");
                var update = new IonosMachineUpdate { Cores = machine.Properties.Cores - 1 };
                await ionos.UpdateMachines(machine.Id, ionosDataCenter, update).ConfigureAwait(false);
            });
        }

        internal static void ScaleMemoryUp(ILogger log, List<WorkloadMachineTuple> totalWorkload, IonosAPI ionos, string ionosDataCenter)
        {
            var dyingMemoryMachines = totalWorkload.Where(m => m.Workload.MemoryUtilization > MAX_ALLOWED_MEMORY_WORKLOAD && m.Machine.Properties.Ram + 1024 <= MAX_WORKER_MEMORY).ToList();
            log.LogInformation($"Found '{dyingMemoryMachines.Count}' machines to scale memory up");
            dyingMemoryMachines.AsParallel().ForAll(async m =>
            {
                var machine = m.Machine;
                log.LogInformation($"Scale memory of '{machine.PrimaryIP}' up");
                var update = new IonosMachineUpdate { Ram = machine.Properties.Ram + 1024 };
                await ionos.UpdateMachines(machine.Id, ionosDataCenter, update).ConfigureAwait(false);
            });
        }
        internal static void ScaleCPUUp(ILogger log, List<WorkloadMachineTuple> totalWorkload, IonosAPI ionos, string ionosDataCenter)
        {
            var dyingCPUMachines = totalWorkload.Where(m => m.Workload.CPUUtilization > MAX_ALLOWED_CPU_WORKLOAD && m.Machine.Properties.Cores + 1 <= MAX_WORKER_CPU).ToList();
            log.LogInformation($"Found '{dyingCPUMachines.Count}' machines to scale cpu up");
            dyingCPUMachines.AsParallel().ForAll(async m =>
            {
                var machine = m.Machine;
                log.LogInformation($"Scale cpu of '{machine.PrimaryIP}' up");
                var update = new IonosMachineUpdate { Cores = machine.Properties.Cores + 1 };
                await ionos.UpdateMachines(machine.Id, ionosDataCenter, update).ConfigureAwait(false);
            });
        }
    }
}

using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using Stateforward.Hsm;

namespace TrafficLightBench
{
    internal sealed class TrafficLight : Instance
    {
        public bool MaintenanceMode { get; set; } = false;
        public int CarsWaiting { get; set; } = 0;
        public int Timer { get; set; } = 0;
    }

    class Program
    {
        private static int EnvInt(string name, int defaultValue)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
        }

        private static bool EnvBool(string name)
        {
            var value = Environment.GetEnvironmentVariable(name);
            return value is not null && value != "" && value != "0" && value != "false" && value != "False";
        }

        private static void AssertTrafficLight(TrafficLight inst, IInstance sm, string state, int carsWaiting, int timer, string step)
        {
            if (sm.State != state)
            {
                throw new InvalidOperationException($"{step}: state {sm.State}, expected {state}");
            }
            if (inst.CarsWaiting != carsWaiting)
            {
                throw new InvalidOperationException($"{step}: CarsWaiting {inst.CarsWaiting}, expected {carsWaiting}");
            }
            if (inst.Timer != timer)
            {
                throw new InvalidOperationException($"{step}: Timer {inst.Timer}, expected {timer}");
            }
        }

        private static async Task ValidateTrafficLight(Model model, Event carArrival, Event timerEvent)
        {
            var ctx = new Context();
            var inst = new TrafficLight();
            var sm = Hsm.Start(ctx, inst, model);
            AssertTrafficLight(inst, sm, "/TrafficLight/operational/red", 0, 0, "initial");

            var completion = sm.Dispatch(carArrival);
            if (completion is null)
            {
                throw new InvalidOperationException("dispatch did not return an awaitable completion");
            }
            await completion.ConfigureAwait(false);
            AssertTrafficLight(inst, sm, "/TrafficLight/operational/red", 1, 0, "after CarArrival");

            await sm.Dispatch(timerEvent).ConfigureAwait(false);
            AssertTrafficLight(inst, sm, "/TrafficLight/operational/green", 1, 40, "after first TimerEvent");

            await sm.Dispatch(timerEvent).ConfigureAwait(false);
            AssertTrafficLight(inst, sm, "/TrafficLight/operational/yellow", 1, 40, "after second TimerEvent");

            await sm.Dispatch(timerEvent).ConfigureAwait(false);
            AssertTrafficLight(inst, sm, "/TrafficLight/operational/red", 1, 40, "after third TimerEvent");

            await sm.Stop().ConfigureAwait(false);
        }

        private static async Task DispatchBatch(IInstance sm, int cycles, Event carArrival, Event timerEvent)
        {
            for (var i = 0; i < cycles; i++)
            {
                await sm.Dispatch(carArrival).ConfigureAwait(false);
                await sm.Dispatch(timerEvent).ConfigureAwait(false);
                await sm.Dispatch(timerEvent).ConfigureAwait(false);
                await sm.Dispatch(timerEvent).ConfigureAwait(false);
            }
        }

        static async Task Main(string[] args)
        {
            var warmupMs = EnvInt("HSM_BENCH_WARMUP_MS", 250);
            var durationMsTarget = EnvInt("HSM_BENCH_DURATION_MS", 2000);

            var model = Hsm.Define("TrafficLight",
                Hsm.Initial(Hsm.Target("operational")),

                Hsm.State("operational",
                    Hsm.Transition(
                        Hsm.On("MaintenanceSwitch"),
                        Hsm.Guard<TrafficLight>((_, inst, _) => inst.MaintenanceMode),
                        Hsm.Target("../maintenance")
                    ),
                    Hsm.Initial(Hsm.Target("red")),

                    Hsm.State("red",
                        Hsm.Transition(
                            Hsm.On("TimerEvent"),
                            Hsm.Guard<TrafficLight>((_, inst, _) => inst.CarsWaiting > 10),
                            Hsm.Effect<TrafficLight>((_, inst, _) => inst.Timer = 60),
                            Hsm.Target("../green")
                        ),
                        Hsm.Transition(
                            Hsm.On("TimerEvent"),
                            Hsm.Effect<TrafficLight>((_, inst, _) => inst.Timer = 40),
                            Hsm.Target("../green")
                        ),
                        Hsm.Transition(
                            Hsm.On("CarArrival"),
                            Hsm.Effect<TrafficLight>((_, inst, _) => inst.CarsWaiting++)
                        )
                    ),

                    Hsm.State("green",
                        Hsm.Transition(
                            Hsm.On("TimerEvent"),
                            Hsm.Target("../yellow")
                        ),
                        Hsm.Transition(
                            Hsm.On("PedestrianButton"),
                            Hsm.Guard<TrafficLight>((_, inst, _) => inst.CarsWaiting == 0),
                            Hsm.Target("../yellow")
                        )
                    ),

                    Hsm.State("yellow",
                        Hsm.Defer("CarArrival"),
                        Hsm.Transition(
                            Hsm.On("TimerEvent"),
                            Hsm.Target("../red")
                        )
                    )
                ),

                Hsm.State("maintenance",
                    Hsm.Entry<TrafficLight>((_, inst, _) => inst.CarsWaiting = 0),
                    Hsm.Transition(
                        Hsm.On("Tick"),
                        Hsm.Effect<TrafficLight>((_, inst, _) => inst.Timer++)
                    ),
                    Hsm.Transition(
                        Hsm.On("MaintenanceSwitch"),
                        Hsm.Guard<TrafficLight>((_, inst, _) => !inst.MaintenanceMode),
                        Hsm.Target("../operational")
                    )
                )
            );

            var carArrival = new Event("CarArrival");
            var timerEvent = new Event("TimerEvent");

            if (EnvBool("HSM_BENCH_VALIDATE"))
            {
                await ValidateTrafficLight(model, carArrival, timerEvent).ConfigureAwait(false);
            }

            var warmupCtx = new Context();
            var warmupInst = new TrafficLight();
            var warmupSm = Hsm.Start(warmupCtx, warmupInst, model);
            var batchCycles = 1;
            while (true)
            {
                var calibration = Stopwatch.StartNew();
                await DispatchBatch(warmupSm, batchCycles, carArrival, timerEvent).ConfigureAwait(false);
                calibration.Stop();
                if (calibration.ElapsedMilliseconds >= 10 || batchCycles >= (1 << 20))
                {
                    break;
                }
                batchCycles *= 2;
            }
            var warmup = Stopwatch.StartNew();
            while (warmup.ElapsedMilliseconds < warmupMs)
            {
                await DispatchBatch(warmupSm, batchCycles, carArrival, timerEvent).ConfigureAwait(false);
            }
            warmup.Stop();
            await warmupSm.Stop().ConfigureAwait(false);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var beforeAlloc = GC.GetAllocatedBytesForCurrentThread();

            var ctx = new Context();
            var inst = new TrafficLight();
            var sm = Hsm.Start(ctx, inst, model);

            var stopwatch = Stopwatch.StartNew();
            var completedCycles = 0;
            while (stopwatch.ElapsedMilliseconds < durationMsTarget)
            {
                await DispatchBatch(sm, batchCycles, carArrival, timerEvent).ConfigureAwait(false);
                completedCycles += batchCycles;
            }
            stopwatch.Stop();
            await sm.Stop().ConfigureAwait(false);

            var afterAlloc = GC.GetAllocatedBytesForCurrentThread();

            var totalDispatches = completedCycles * 4L;
            var durationMs = stopwatch.Elapsed.TotalMilliseconds;
            var opsPerSec = (long)(totalDispatches / stopwatch.Elapsed.TotalSeconds);
            var allocMB = (afterAlloc - beforeAlloc) / (1024.0 * 1024.0);

            var result = new
            {
                language = "C#",
                iterations = totalDispatches,
                duration_ms = Math.Round(durationMs),
                memory_mb = Math.Round(allocMB, 3),
                throughput_ops_per_sec = opsPerSec
            };

            Console.WriteLine(JsonSerializer.Serialize(result));
        }
    }
}

using System;
using System.Threading;
using FluentAssertions;
using NUnit.Framework;
using NzbDrone.Core.MediaFiles.EpisodeImport;
using NzbDrone.Core.Test.Framework;

namespace NzbDrone.Core.Test.MediaFiles.EpisodeImport
{
    [TestFixture]
    public class ImportProbePoolFixture : CoreTest
    {
        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", null);
            Environment.SetEnvironmentVariable("IMPORT_PROBE_TIMEOUT", null);
        }

        [Test]
        public void top_level_run_reaches_configured_degree()
        {
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "4");

            // A gate that releases once four bodies are in flight at the same time, proving a top-level
            // Run fans out to the configured degree (the nesting guard must not throttle it).
            var gate = new LeafConcurrencyGate(releaseThreshold: 4, timeout: TimeSpan.FromSeconds(5));

            ImportProbePool.Run(4, _ => gate.Enter());

            gate.PeakConcurrency.Should().Be(4);
        }

        [Test]
        public void nested_run_bounds_leaf_concurrency_to_outer_degree()
        {
            Environment.SetEnvironmentVariable("IMPORT_PROBE_THREADS", "4");

            const int outerDegree = 4;
            const int outerCount = 4;
            const int innerCount = 4;

            // Threshold above the outer degree: only reachable if a nested Run adds its own parallelism on
            // top of the outer fan-out. The nesting guard forces nested Runs serial, so total leaf
            // concurrency can never exceed the outer degree and the gate falls through on timeout at a
            // peak of outerDegree. Without the guard the leaves reach outerDegree * innerDegree.
            var gate = new LeafConcurrencyGate(releaseThreshold: outerDegree + 1, timeout: TimeSpan.FromSeconds(2));

            ImportProbePool.Run(outerCount, _ =>
            {
                ImportProbePool.Run(innerCount, __ => gate.Enter());
            });

            gate.PeakConcurrency.Should().BeLessOrEqualTo(outerDegree,
                "the nesting guard must run nested probe pools serially so total leaf concurrency stays bounded by the outer degree");
        }

        // Records peak observed concurrency. Callers block until releaseThreshold of them are in flight at
        // once (then all release), or until the timeout elapses. A run that can never reach the threshold
        // simply falls through on the timeout, leaving PeakConcurrency at the true maximum overlap.
        private sealed class LeafConcurrencyGate
        {
            private readonly int _releaseThreshold;
            private readonly TimeSpan _timeout;
            private readonly object _sync = new object();
            private int _current;
            private bool _released;

            public LeafConcurrencyGate(int releaseThreshold, TimeSpan timeout)
            {
                _releaseThreshold = releaseThreshold;
                _timeout = timeout;
            }

            public int PeakConcurrency { get; private set; }

            public void Enter()
            {
                lock (_sync)
                {
                    _current++;

                    if (_current > PeakConcurrency)
                    {
                        PeakConcurrency = _current;
                    }

                    if (_current >= _releaseThreshold)
                    {
                        _released = true;
                        Monitor.PulseAll(_sync);
                    }
                    else
                    {
                        var deadline = DateTime.UtcNow + _timeout;

                        while (!_released)
                        {
                            var remaining = deadline - DateTime.UtcNow;

                            if (remaining <= TimeSpan.Zero)
                            {
                                break;
                            }

                            Monitor.Wait(_sync, remaining);
                        }
                    }

                    _current--;
                }
            }
        }
    }
}

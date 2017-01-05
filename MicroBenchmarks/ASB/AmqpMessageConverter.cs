using System;
using System.Collections.Generic;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnostics.Windows;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;

namespace MicroBenchmarks.ASB
{
    [Config(typeof(Config))]
    public class AmqpMessageConverter
    {
        private List<AmqpMessageConverterBefore.BrokeredMessage> messagesBefore;
        private List<AmqpMessageConverterAfter.BrokeredMessage> messagesAfter;

        private class Config : ManualConfig
        {
            public Config()
            {
                Add(MarkdownExporter.GitHub);
                Add(new MemoryDiagnoser());
                Add(Job.Default.With(Platform.X64));
                Add(Job.Default.With(Platform.X86));
            }
        }

        [Params(1, 2, 4, 8, 16, 32, 64, 128)]
        public int Messages { get; set; }

        [Params(1, 100, 1000, 10000)]
        public int Calls { get; set; }

        [Setup]
        public void SetUp()
        {
            messagesBefore = new List<AmqpMessageConverterBefore.BrokeredMessage>(Messages);
            for (int i = 0; i < Messages; i++)
            {
                messagesBefore.Add(new AmqpMessageConverterBefore.BrokeredMessage(new SomeMessage { MyProperty = i }));
            }

            messagesAfter = new List<AmqpMessageConverterAfter.BrokeredMessage>(Messages);
            for (int i = 0; i < Messages; i++)
            {
                messagesAfter.Add(new AmqpMessageConverterAfter.BrokeredMessage(new SomeMessage { MyProperty = i }));
            }
        }

        public class SomeMessage
        {
            public int MyProperty { get; set; }
        }

        [Benchmark(Baseline = true)]
        public void ConverterBeforeOptimizations()
        {
            for (int i = 0; i < Calls; i++)
            {
                var message =
                    AmqpMessageConverterBefore.AmqpMessageConverter.BrokeredMessagesToAmqpMessage(messagesBefore, false);

                GC.KeepAlive(message);
            }
        }

        [Benchmark]
        public void ConverterAfterOptimizations()
        {
            for (int i = 0; i < Calls; i++)
            {
                var message =
                    AmqpMessageConverterAfter.AmqpMessageConverter.BrokeredMessagesToAmqpMessage(messagesAfter, false);

                GC.KeepAlive(message);
            }
        }
    }
}
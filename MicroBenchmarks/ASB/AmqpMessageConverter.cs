using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnostics.Windows;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using MicroBenchmarks.NServiceBus;
using Microsoft.Azure.Amqp;

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
                //Add(Job.Default.With(Mode.SingleRun).With(Platform.X64).WithLaunchCount(1).WithWarmupCount(1).WithTargetCount(1));
                Add(Job.Default.With(Mode.SingleRun).With(Platform.X86).WithLaunchCount(1).WithWarmupCount(1).WithTargetCount(1));
            }
        }
        [Params(1, 2, 4, 8, 64, 128)]
        public int Messages { get; set; }

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
        public AmqpMessage ConverterBeforeOptimizations()
        {
            return AmqpMessageConverterBefore.AmqpMessageConverter.BrokeredMessagesToAmqpMessage(messagesBefore, false);
        }

        [Benchmark()]
        public AmqpMessage ConverterAfterOptimizations()
        {
            return AmqpMessageConverterAfter.AmqpMessageConverter.BrokeredMessagesToAmqpMessage(messagesAfter, false);
        }
    }

}
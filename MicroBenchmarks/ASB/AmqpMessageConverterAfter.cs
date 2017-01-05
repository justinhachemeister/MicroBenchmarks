using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using Microsoft.Azure.Amqp;
using Microsoft.Azure.Amqp.Encoding;
using Microsoft.Azure.Amqp.Framing;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace MicroBenchmarks.ASB
{
    public class AmqpMessageConverterAfter
    {
        public enum MappingType
        {
            ApplicationProperty,
            MessageBody
        }

        // Copyright (c) Microsoft. All rights reserved.
        // Licensed under the MIT license. See LICENSE file in the project root for full license information.

        // WARNING: Consult filter engine owner before modifying this enum.
        // Introducing a new member here has impact to filtering engine in data type precedence and data conversion.
        // ALWASYS insert new types before Unknown!
        enum PropertyValueType
        {
            Null,
            Byte, SByte, Char, Int16, UInt16, Int32, UInt32, Int64, UInt64, Single, Double, Decimal, // Numeric types
            Boolean, Guid, String, Uri, DateTime, DateTimeOffset, TimeSpan,
            Stream,
            Unknown,
        }

        class SerializationUtilities
        {
            static readonly Dictionary<Type, PropertyValueType> TypeToIntMap = new Dictionary<Type, PropertyValueType>
        {
            { typeof(byte), PropertyValueType.Byte },
            { typeof(sbyte), PropertyValueType.SByte },
            { typeof(char), PropertyValueType.Char },
            { typeof(short), PropertyValueType.Int16 },
            { typeof(ushort), PropertyValueType.UInt16 },
            { typeof(int), PropertyValueType.Int32 },
            { typeof(uint), PropertyValueType.UInt32 },
            { typeof(long), PropertyValueType.Int64 },
            { typeof(ulong), PropertyValueType.UInt64 },
            { typeof(float), PropertyValueType.Single },
            { typeof(double), PropertyValueType.Double },
            { typeof(decimal), PropertyValueType.Decimal },
            { typeof(bool), PropertyValueType.Boolean },
            { typeof(Guid), PropertyValueType.Guid },
            { typeof(string), PropertyValueType.String },
            { typeof(Uri), PropertyValueType.Uri },
            { typeof(DateTime), PropertyValueType.DateTime },
            { typeof(DateTimeOffset), PropertyValueType.DateTimeOffset },
            { typeof(TimeSpan), PropertyValueType.TimeSpan },
            ////{ typeof(BufferedInputStream), PropertyValueType.Stream },
        };

            public static PropertyValueType GetTypeId(object value)
            {
                if (value == null)
                {
                    return PropertyValueType.Null;
                }

                PropertyValueType typeId;
                if (TypeToIntMap.TryGetValue(value.GetType(), out typeId))
                {
                    return typeId;
                }

                return PropertyValueType.Unknown;
            }
        }

        public static class AmqpMessageConverter
        {
            public const string EnqueuedTimeUtcName = "x-opt-enqueued-time";
            public const string ScheduledEnqueueTimeUtcName = "x-opt-scheduled-enqueue-time";
            public const string SequenceNumberName = "x-opt-sequence-number";
            public const string OffsetName = "x-opt-offset";
            public const string LockTokenName = "x-opt-lock-token";
            public const string LockedUntilName = "x-opt-locked-until";
            public const string PublisherName = "x-opt-publisher";
            public const string PartitionKeyName = "x-opt-partition-key";
            public const string PartitionIdName = "x-opt-partition-id";
            public const string PrefilteredMessageHeadersName = "x-opt-prefiltered-headers";
            public const string PrefilteredMessagePropertiesName = "x-opt-prefiltered-properties";
            public const string DeadLetterSourceName = "x-opt-deadletter-source";
            public const string TimeSpanName = AmqpConstants.Vendor + ":timespan";
            public const string UriName = AmqpConstants.Vendor + ":uri";
            public const string DateTimeOffsetName = AmqpConstants.Vendor + ":datetime-offset";
            const int GuidSize = 16;

            public static AmqpMessage BrokeredMessagesToAmqpMessage(IEnumerable<BrokeredMessage> brokeredMessages, bool batchable)
            {
                AmqpMessage previous = null, current = null;
                BrokeredMessage firstBrokeredMessage = null;
                List<Data> dataList = null;
                foreach (var brokeredMessage in brokeredMessages)
                {
                    previous = current;

                    if (firstBrokeredMessage == null)
                    {
                        firstBrokeredMessage = brokeredMessage;
                    }

                    current = ClientGetMessage(brokeredMessage);

                    if (previous == null)
                    {
                        continue;
                    }

                    if (dataList == null)
                    {
                        dataList = new List<Data> { ToData(previous) };
                    }

                    dataList.Add(ToData(current));
                }

                if (previous == null && current != null)
                {
                    current.Batchable = batchable;
                    return current;
                }

                current = AmqpMessage.Create(dataList);
                current.MessageFormat = AmqpConstants.AmqpBatchedMessageFormat;

                // ReSharper disable once PossibleNullReferenceException
                if (firstBrokeredMessage.MessageId != null)
                {
                    current.Properties.MessageId = firstBrokeredMessage.MessageId;
                }

                if (firstBrokeredMessage.SessionId != null)
                {
                    current.Properties.GroupId = firstBrokeredMessage.SessionId;
                }

                if (firstBrokeredMessage.PartitionKey != null)
                {
                    current.MessageAnnotations.Map[AmqpMessageConverter.PartitionKeyName] = firstBrokeredMessage.PartitionKey;
                }

                current.Batchable = batchable;
                return current;
            }

            private static Data ToData(AmqpMessage message)
            {
                ArraySegment<byte>[] payload = message.GetPayload();
                BufferListStream buffer = new BufferListStream(payload);
                ArraySegment<byte> value = buffer.ReadBytes((int)buffer.Length);
                return new Data { Value = value };
            }

            // return from AMQP lib to client API for a received message
            // TODO: expose other AMQP sections in BrokeredMessage
            public static BrokeredMessage ClientGetMessage(AmqpMessage amqpMessage)
            {
                BrokeredMessage brokeredMessage;

                if ((amqpMessage.BodyType & SectionFlag.Data) != 0 ||
                    (amqpMessage.BodyType & SectionFlag.AmqpSequence) != 0)
                {
                    Stream bodyStream = AmqpMessageConverter.GetMessageBodyStream(amqpMessage);
                    brokeredMessage = new BrokeredMessage(bodyStream, true);
                }
                else if ((amqpMessage.BodyType & SectionFlag.AmqpValue) != 0)
                {
                    object netObject;
                    if (!TryGetNetObjectFromAmqpObject(amqpMessage.ValueBody.Value, MappingType.MessageBody, out netObject))
                    {
                        netObject = amqpMessage.ValueBody.Value;
                    }

                    brokeredMessage = new BrokeredMessage(netObject, amqpMessage.BodyStream);
                }
                else
                {
                    brokeredMessage = new BrokeredMessage();
                }

                SectionFlag sections = amqpMessage.Sections;
                if ((sections & SectionFlag.Header) != 0)
                {
                    if (amqpMessage.Header.Ttl != null)
                    {
                        brokeredMessage.TimeToLive = TimeSpan.FromMilliseconds(amqpMessage.Header.Ttl.Value);
                    }

                    if (amqpMessage.Header.DeliveryCount != null)
                    {
                        brokeredMessage.DeliveryCount = (int)(amqpMessage.Header.DeliveryCount.Value + 1);
                    }
                }

                if ((sections & SectionFlag.Properties) != 0)
                {
                    if (amqpMessage.Properties.MessageId != null)
                    {
                        brokeredMessage.MessageId = amqpMessage.Properties.MessageId.ToString();
                    }

                    if (amqpMessage.Properties.CorrelationId != null)
                    {
                        brokeredMessage.CorrelationId = amqpMessage.Properties.CorrelationId.ToString();
                    }

                    if (amqpMessage.Properties.ContentType.Value != null)
                    {
                        brokeredMessage.ContentType = amqpMessage.Properties.ContentType.Value;
                    }

                    if (amqpMessage.Properties.Subject != null)
                    {
                        brokeredMessage.Label = amqpMessage.Properties.Subject;
                    }

                    if (amqpMessage.Properties.To != null)
                    {
                        brokeredMessage.To = amqpMessage.Properties.To.ToString();
                    }

                    if (amqpMessage.Properties.ReplyTo != null)
                    {
                        brokeredMessage.ReplyTo = amqpMessage.Properties.ReplyTo.ToString();
                    }

                    if (amqpMessage.Properties.GroupId != null)
                    {
                        brokeredMessage.SessionId = amqpMessage.Properties.GroupId;
                    }

                    if (amqpMessage.Properties.ReplyToGroupId != null)
                    {
                        brokeredMessage.ReplyToSessionId = amqpMessage.Properties.ReplyToGroupId;
                    }
                }

                // Do applicaiton properties before message annotations, because the application properties
                // can be updated by entries from message annotation.
                if ((sections & SectionFlag.ApplicationProperties) != 0)
                {
                    foreach (var pair in amqpMessage.ApplicationProperties.Map)
                    {
                        object netObject;
                        if (TryGetNetObjectFromAmqpObject(pair.Value, MappingType.ApplicationProperty, out netObject))
                        {
                            brokeredMessage.Properties[pair.Key.ToString()] = netObject;
                        }
                    }
                }

                if ((sections & SectionFlag.MessageAnnotations) != 0)
                {
                    foreach (var pair in amqpMessage.MessageAnnotations.Map)
                    {
                        string key = pair.Key.ToString();
                        switch (key)
                        {
                            case EnqueuedTimeUtcName:
                                brokeredMessage.EnqueuedTimeUtc = (DateTime)pair.Value;
                                break;
                            case ScheduledEnqueueTimeUtcName:
                                brokeredMessage.ScheduledEnqueueTimeUtc = (DateTime)pair.Value;
                                break;
                            case SequenceNumberName:
                                brokeredMessage.SequenceNumber = (long)pair.Value;
                                break;
                            case OffsetName:
                                brokeredMessage.EnqueuedSequenceNumber = long.Parse((string)pair.Value);
                                break;
                            case LockedUntilName:
                                brokeredMessage.LockedUntilUtc = (DateTime)pair.Value;
                                break;
                            case PublisherName:
                                brokeredMessage.Publisher = (string)pair.Value;
                                break;
                            case PartitionKeyName:
                                brokeredMessage.PartitionKey = (string)pair.Value;
                                break;
                            case PartitionIdName:
                                brokeredMessage.PartitionId = (short)pair.Value;
                                break;
                            case DeadLetterSourceName:
                                brokeredMessage.DeadLetterSource = (string)pair.Value;
                                break;
                            default:
                                object netObject;
                                if (TryGetNetObjectFromAmqpObject(pair.Value, MappingType.ApplicationProperty, out netObject))
                                {
                                    brokeredMessage.Properties[key] = netObject;
                                }
                                break;
                        }
                    }
                }

                if (amqpMessage.DeliveryTag.Count == GuidSize)
                {
                    byte[] guidBuffer = new byte[GuidSize];
                    Buffer.BlockCopy(amqpMessage.DeliveryTag.Array, amqpMessage.DeliveryTag.Offset, guidBuffer, 0, GuidSize);
                    brokeredMessage.LockToken = new Guid(guidBuffer);
                }

                brokeredMessage.AttachDisposables(new[] { amqpMessage });

                return brokeredMessage;
            }

            // return from Client API to AMQP lib for send
            public static AmqpMessage ClientGetMessage(BrokeredMessage brokeredMessage)
            {
                AmqpMessage amqpMessage = AmqpMessageConverter.CreateAmqpMessageFromSbmpMessage(brokeredMessage);

                amqpMessage.Properties.MessageId = brokeredMessage.MessageId;
                amqpMessage.Properties.CorrelationId = brokeredMessage.CorrelationId;
                amqpMessage.Properties.ContentType = brokeredMessage.ContentType;
                amqpMessage.Properties.Subject = brokeredMessage.Label;
                amqpMessage.Properties.To = brokeredMessage.To;
                amqpMessage.Properties.ReplyTo = brokeredMessage.ReplyTo;
                amqpMessage.Properties.GroupId = brokeredMessage.SessionId;
                amqpMessage.Properties.ReplyToGroupId = brokeredMessage.ReplyToSessionId;

                if ((brokeredMessage.InitializedMembers & BrokeredMessage.MessageMembers.TimeToLive) != 0)
                {
                    amqpMessage.Header.Ttl = (uint)brokeredMessage.TimeToLive.TotalMilliseconds;
                    amqpMessage.Properties.CreationTime = DateTime.UtcNow;

                    if (AmqpConstants.MaxAbsoluteExpiryTime - amqpMessage.Properties.CreationTime.Value > brokeredMessage.TimeToLive)
                    {
                        amqpMessage.Properties.AbsoluteExpiryTime = amqpMessage.Properties.CreationTime.Value + brokeredMessage.TimeToLive;
                    }
                    else
                    {
                        amqpMessage.Properties.AbsoluteExpiryTime = AmqpConstants.MaxAbsoluteExpiryTime;
                    }
                }

                if ((brokeredMessage.InitializedMembers & BrokeredMessage.MessageMembers.ScheduledEnqueueTimeUtc) != 0 &&
                    brokeredMessage.ScheduledEnqueueTimeUtc > DateTime.MinValue)
                {
                    amqpMessage.MessageAnnotations.Map.Add(ScheduledEnqueueTimeUtcName, brokeredMessage.ScheduledEnqueueTimeUtc);
                }

                if ((brokeredMessage.InitializedMembers & BrokeredMessage.MessageMembers.Publisher) != 0 &&
                    brokeredMessage.Publisher != null)
                {
                    amqpMessage.MessageAnnotations.Map.Add(PublisherName, brokeredMessage.Publisher);
                }

                if ((brokeredMessage.InitializedMembers & BrokeredMessage.MessageMembers.DeadLetterSource) != 0 &&
                    brokeredMessage.DeadLetterSource != null)
                {
                    amqpMessage.MessageAnnotations.Map.Add(DeadLetterSourceName, brokeredMessage.DeadLetterSource);
                }

                if ((brokeredMessage.InitializedMembers & BrokeredMessage.MessageMembers.PartitionKey) != 0 &&
                    brokeredMessage.PartitionKey != null)
                {
                    amqpMessage.MessageAnnotations.Map.Add(PartitionKeyName, brokeredMessage.PartitionKey);
                }

                foreach (KeyValuePair<string, object> pair in brokeredMessage.Properties)
                {
                    object amqpObject;
                    if (TryGetAmqpObjectFromNetObject(pair.Value, MappingType.ApplicationProperty, out amqpObject))
                    {
                        amqpMessage.ApplicationProperties.Map.Add(pair.Key, amqpObject);
                    }
                }

                return amqpMessage;
            }

            public static AmqpMessage CreateAmqpMessageFromSbmpMessage(BrokeredMessage brokeredMessage)
            {
                AmqpMessage amqpMessage;

                object bodyObject = brokeredMessage.ClearBodyObject();
                object mappedBodyObject = null;

                if (bodyObject != null)
                {
                    TryGetAmqpObjectFromNetObject(bodyObject, MappingType.MessageBody, out mappedBodyObject);
                }

                if (mappedBodyObject != null)
                {
                    amqpMessage = AmqpMessage.Create(new AmqpValue() { Value = mappedBodyObject });
                }
                else if (brokeredMessage.BodyStream != null)
                {
                    if (brokeredMessage.BodyStream.CanSeek && brokeredMessage.BodyStream.Position != 0)
                    {
                        // TODO:throw new InvalidOperationException(SRClient.CannotSerializeMessageWithPartiallyConsumedBodyStream);
                        throw new InvalidOperationException("CannotSerializeMessageWithPartiallyConsumedBodyStream");
                    }

                    amqpMessage = AmqpMessage.Create(brokeredMessage.BodyStream, false);
                }
                else
                {
                    amqpMessage = AmqpMessage.Create();
                }

                return amqpMessage;
            }

            public static bool TryGetAmqpObjectFromNetObject(object netObject, MappingType mappingType, out object amqpObject)
            {
                amqpObject = null;
                if (netObject == null)
                {
                    return true;
                }

                switch (SerializationUtilities.GetTypeId(netObject))
                {
                    case PropertyValueType.Byte:
                    case PropertyValueType.SByte:
                    case PropertyValueType.Int16:
                    case PropertyValueType.Int32:
                    case PropertyValueType.Int64:
                    case PropertyValueType.UInt16:
                    case PropertyValueType.UInt32:
                    case PropertyValueType.UInt64:
                    case PropertyValueType.Single:
                    case PropertyValueType.Double:
                    case PropertyValueType.Boolean:
                    case PropertyValueType.Decimal:
                    case PropertyValueType.Char:
                    case PropertyValueType.Guid:
                    case PropertyValueType.DateTime:
                    case PropertyValueType.String:
                        amqpObject = netObject;
                        break;
                    case PropertyValueType.Stream:
                        if (mappingType == MappingType.ApplicationProperty)
                        {
                            amqpObject = StreamToBytes((Stream)netObject);
                        }
                        break;
                    case PropertyValueType.Uri:
                        amqpObject = new DescribedType((AmqpSymbol)UriName, ((Uri)netObject).AbsoluteUri);
                        break;
                    case PropertyValueType.DateTimeOffset:
                        amqpObject = new DescribedType((AmqpSymbol)DateTimeOffsetName, ((DateTimeOffset)netObject).UtcTicks);
                        break;
                    case PropertyValueType.TimeSpan:
                        amqpObject = new DescribedType((AmqpSymbol)TimeSpanName, ((TimeSpan)netObject).Ticks);
                        break;
                    case PropertyValueType.Unknown:
                        if (netObject is Stream)
                        {
                            if (mappingType == MappingType.ApplicationProperty)
                            {
                                amqpObject = StreamToBytes((Stream)netObject);
                            }
                        }
                        else if (mappingType == MappingType.ApplicationProperty)
                        {
                            // TODO: throw FxTrace.Exception.AsError(new SerializationException(SRClient.FailedToSerializeUnsupportedType(netObject.GetType().FullName)));
                            throw new SerializationException("netObject.GetType().FullName");
                        }
                        else if (netObject is byte[])
                        {
                            amqpObject = new ArraySegment<byte>((byte[])netObject);
                        }
                        else if (netObject is IList)
                        {
                            // Array is also an IList
                            amqpObject = netObject;
                        }
                        else if (netObject is IDictionary)
                        {
                            amqpObject = new AmqpMap((IDictionary)netObject);
                        }
                        break;
                    default:
                        break;
                }

                return amqpObject != null;
            }

            public static bool TryGetNetObjectFromAmqpObject(object amqpObject, MappingType mappingType, out object netObject)
            {
                netObject = null;
                if (amqpObject == null)
                {
                    return true;
                }

                switch (SerializationUtilities.GetTypeId(amqpObject))
                {
                    case PropertyValueType.Byte:
                    case PropertyValueType.SByte:
                    case PropertyValueType.Int16:
                    case PropertyValueType.Int32:
                    case PropertyValueType.Int64:
                    case PropertyValueType.UInt16:
                    case PropertyValueType.UInt32:
                    case PropertyValueType.UInt64:
                    case PropertyValueType.Single:
                    case PropertyValueType.Double:
                    case PropertyValueType.Boolean:
                    case PropertyValueType.Decimal:
                    case PropertyValueType.Char:
                    case PropertyValueType.Guid:
                    case PropertyValueType.DateTime:
                    case PropertyValueType.String:
                        netObject = amqpObject;
                        break;
                    case PropertyValueType.Unknown:
                        if (amqpObject is AmqpSymbol)
                        {
                            netObject = ((AmqpSymbol)amqpObject).Value;
                        }
                        else if (amqpObject is ArraySegment<byte>)
                        {
                            ArraySegment<byte> binValue = (ArraySegment<byte>)amqpObject;
                            if (binValue.Count == binValue.Array.Length)
                            {
                                netObject = binValue.Array;
                            }
                            else
                            {
                                byte[] buffer = new byte[binValue.Count];
                                Buffer.BlockCopy(binValue.Array, binValue.Offset, buffer, 0, binValue.Count);
                                netObject = buffer;
                            }
                        }
                        else if (amqpObject is DescribedType)
                        {
                            DescribedType describedType = (DescribedType)amqpObject;
                            if (describedType.Descriptor is AmqpSymbol)
                            {
                                AmqpSymbol symbol = (AmqpSymbol)describedType.Descriptor;
                                if (symbol.Equals(UriName))
                                {
                                    netObject = new Uri((string)describedType.Value);
                                }
                                else if (symbol.Equals(TimeSpanName))
                                {
                                    netObject = new TimeSpan((long)describedType.Value);
                                }
                                else if (symbol.Equals(DateTimeOffsetName))
                                {
                                    netObject = new DateTimeOffset(new DateTime((long)describedType.Value, DateTimeKind.Utc));
                                }
                            }
                        }
                        else if (mappingType == MappingType.ApplicationProperty)
                        {
                            // TODO: throw FxTrace.Exception.AsError(new SerializationException(SRClient.FailedToSerializeUnsupportedType(amqpObject.GetType().FullName)));
                            throw new SerializationException("netObject.GetType().FullName");
                        }
                        else if (amqpObject is AmqpMap)
                        {
                            AmqpMap map = (AmqpMap)amqpObject;
                            Dictionary<string, object> dictionary = new Dictionary<string, object>();
                            foreach (var pair in map)
                            {
                                dictionary.Add(pair.Key.ToString(), pair.Value);
                            }

                            netObject = dictionary;
                        }
                        else
                        {
                            netObject = amqpObject;
                        }
                        break;
                    default:
                        break;
                }

                return netObject != null;
            }

            public static ArraySegment<byte> StreamToBytes(Stream stream)
            {
                MemoryStream memoryStream = new MemoryStream();
                int bytesRead;
                byte[] readBuffer = new byte[512];
                while ((bytesRead = stream.Read(readBuffer, 0, readBuffer.Length)) > 0)
                {
                    memoryStream.Write(readBuffer, 0, bytesRead);
                }

                ArraySegment<byte> buffer = new ArraySegment<byte>(memoryStream.ToArray());

                memoryStream.Dispose();
                return buffer;
            }

            static Stream GetMessageBodyStream(AmqpMessage message)
            {
                if ((message.BodyType & SectionFlag.Data) != 0 &&
                    message.DataBody != null)
                {
                    List<ArraySegment<byte>> dataSegments = new List<ArraySegment<byte>>();
                    foreach (Data data in message.DataBody)
                    {
                        dataSegments.Add((ArraySegment<byte>)data.Value);
                    }

                    return new BufferListStream(dataSegments.ToArray());
                }

                return null;
            }
        }

        sealed class DataContractBinarySerializer : XmlObjectSerializer
        {
            readonly DataContractSerializer dataContractSerializer;

            public DataContractBinarySerializer(Type type)
            {
                this.dataContractSerializer = new DataContractSerializer(type);
            }

            // Override the default (Text) and use Binary Xml Reader instead
            public override object ReadObject(Stream stream)
            {
                if (stream == null)
                {
                    //throw Fx.Exception.ArgumentNull(nameof(stream));
                }

                return this.ReadObject(XmlDictionaryReader.CreateBinaryReader(stream, XmlDictionaryReaderQuotas.Max));
            }

            // Override the default (Text) and use Binary Xml Writer instead
            public override void WriteObject(Stream stream, object graph)
            {
                if (stream == null)
                {
                    //throw Fx.Exception.ArgumentNull(nameof(stream));
                }

                XmlDictionaryWriter writer = XmlDictionaryWriter.CreateBinaryWriter(stream, null, null, false);
                this.WriteObject(writer, graph);
                writer.Flush();
            }

            public override void WriteObject(XmlDictionaryWriter writer, object graph)
            {
                if (writer == null)
                {
                    //throw Fx.Exception.ArgumentNull(nameof(writer));
                }

                this.dataContractSerializer.WriteObject(writer, graph);
            }

            // All the methods below this point simply delegate to the DataContractSerializer implementation
            public override bool IsStartObject(XmlDictionaryReader reader)
            {
                return this.dataContractSerializer.IsStartObject(reader);
            }

            public override object ReadObject(XmlDictionaryReader reader, bool verifyObjectName)
            {
                return this.dataContractSerializer.ReadObject(reader, verifyObjectName);
            }

            public override void WriteEndObject(XmlDictionaryWriter writer)
            {
                this.dataContractSerializer.WriteEndObject(writer);
            }

            public override void WriteObjectContent(XmlDictionaryWriter writer, object graph)
            {
                this.dataContractSerializer.WriteObjectContent(writer, graph);
            }

            public override void WriteStartObject(XmlDictionaryWriter writer, object graph)
            {
                this.dataContractSerializer.WriteStartObject(writer, graph);
            }
        }

        /// <summary>Represents the unit of communication between ServiceBus client and Service.</summary>
        public sealed class BrokeredMessage : IDisposable
        {
            static Func<string> messageIdGeneratorFunc = () => (string)null;

            readonly object disposablesSyncObject = new object();
            readonly bool ownsBodyStream;
            readonly bool bodyObjectDecoded;

            long bodyId;
            long bodySize;
            Stream bodyStream;
            object bodyObject;
            string contentType;
            string correlationId;
            bool disposed;
            string deadLetterSource;
            DateTime enqueuedTimeUtc;
            int getBodyCalled;
            long headerSize;
            MessageMembers initializedMembers;
            string label;
            int messageConsumed;
            string messageId;
            short partitionId;
            string partitionKey;
            IDictionary<string, object> properties;
            string publisher;

            // TODO: ReceiveContext receiveContext;
            ReceiverHeaders receiverHeaders;
            string replyTo;
            string replyToSessionId;
            DateTime scheduledEnqueueTimeUtc = DateTime.MinValue;
            string sessionId;
            TimeSpan timeToLive;
            string to;
            string viaPartitionKey;

            // TODO: Check back to see if this can be safely removed
            volatile List<IDisposable> attachedDisposables;

            /// <summary>Initializes a new instance of the <see cref="BrokeredMessage" /> class.</summary>
            public BrokeredMessage()
            {
                try
                {
                    if (messageIdGeneratorFunc != null)
                    {
                        this.messageId = messageIdGeneratorFunc();
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("BrokeredMessage ID generator function has failed.", ex);
                }
            }

            /// <summary>Initializes a new instance of the
            /// <see cref="BrokeredMessage" /> class from a given object by using DataContractSerializer with a binary XmlDictionaryWriter.</summary>
            /// <param name="serializableObject">The serializable object.</param>
            public BrokeredMessage(object serializableObject)
                : this(serializableObject, serializableObject == null ? null : new DataContractBinarySerializer(GetObjectType(serializableObject)))
            {
                this.bodyObject = serializableObject;
            }

            /// <summary> Constructor that creates a BrokeredMessage from a given object using the provided XmlObjectSerializer </summary>
            /// <remarks> You should be aware of the exceptions that their provided Serializer can throw and take appropriate
            /// actions. Please refer to <see href="http://msdn.microsoft.com/en-us/library/ms574055.aspx"/> for
            /// a possible list of exceptions and their cause. </remarks>
            /// <param name="serializableObject"> The serializable object. </param>
            /// <param name="serializer">         The serializer object. </param>
            /// <exception cref="ArgumentNullException">Thrown when null serializer is passed to the method
            /// TODO:
            /// with a non-null serializableObject</exception>
            public BrokeredMessage(object serializableObject, XmlObjectSerializer serializer)
                : this()
            {
                if (serializableObject != null)
                {
                    if (serializer == null)
                    {
                        ////throw FxTrace.Exception.AsError(new ArgumentNullException("serializer"));
                        throw new ArgumentNullException(nameof(serializer));
                    }

                    MemoryStream stream = new MemoryStream(256);
                    serializer.WriteObject(stream, serializableObject);
                    stream.Flush();
                    stream.Position = 0;
                    this.BodyStream = stream;
                    this.ownsBodyStream = true;
                }
            }

            /// <summary>Initializes a new instance of the <see cref="BrokeredMessage" /> class.</summary>
            /// <param name="messageBodyStream">The message body stream.</param>
            public BrokeredMessage(Stream messageBodyStream)
                : this(messageBodyStream, false)
            {
            }

            /// <summary>Initializes a new instance of the
            /// <see cref="BrokeredMessage" /> class using the supplied stream as its body.</summary>
            /// <param name="messageBodyStream">The message body stream.</param>
            /// <param name="ownsStream">true to indicate that the stream will be closed when the message is
            /// closed; false to indicate that the stream will not be closed when the message is closed.</param>
            public BrokeredMessage(Stream messageBodyStream, bool ownsStream)
                : this()
            {
                this.ownsBodyStream = ownsStream;
                this.BodyStream = messageBodyStream;
            }

            internal BrokeredMessage(object bodyObject, Stream bodyStream)
                : this()
            {
                this.bodyObject = bodyObject;
                this.bodyObjectDecoded = true;
                this.bodyStream = bodyStream;
                this.ownsBodyStream = true;
            }

            BrokeredMessage(BrokeredMessage originalMessage, bool clientSideCloning)
            {
                this.CopyMessageHeaders(originalMessage, clientSideCloning);

                this.bodyObject = originalMessage.bodyObject;
                this.bodyObjectDecoded = originalMessage.bodyObjectDecoded;
                Stream originalStream = originalMessage.BodyStream;
                if (originalStream != null)
                {
                    this.BodyStream = BrokeredMessage.CloneStream(originalMessage.BodyStream, clientSideCloning);
                    this.ownsBodyStream = true;
                }

                this.AttachDisposables(BrokeredMessage.CloneDisposables(originalMessage.attachedDisposables));
            }

            [Flags]
            internal enum MessageMembers : int
            {
                // public get/set members
                MessageId = 1,
                CorrelationId = 1 << 1,
                To = 1 << 2,
                ReplyTo = 1 << 3,
                TimeToLive = 1 << 4,
                SessionId = 1 << 5,
                Label = 1 << 6,
                ContentType = 1 << 7,
                ScheduledEnqueueTimeUtc = 1 << 8,
                PartitionKey = 1 << 9,
                ReplyToSessionId = 1 << 10,
                ViaPartitionKey = 1 << 11,

                // public read-only members
                DeadLetterSource = 1 << 14,
                Publisher = 1 << 15,
                EnqueuedTimeUtc = 1 << 16,
                SequenceNumber = 1 << 17,
                LockToken = 1 << 18,
                LockedUntilUtc = 1 << 19,
                DeliveryCount = 1 << 20,
                MessageState = 1 << 21,
                EnqueuedSequenceNumber = 1 << 22,

                // internal
                PartitionId = 1 << 23
            }

            /// <summary>Gets or sets the identifier of the correlation.</summary>
            /// <value>The identifier of the correlation.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            public string CorrelationId
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.correlationId;
                }

                set
                {
                    this.ThrowIfDisposed();
                    this.correlationId = value;
                    if (value == null)
                    {
                        this.ClearInitializedMember(MessageMembers.CorrelationId);
                    }
                    else
                    {
                        this.initializedMembers |= MessageMembers.CorrelationId;
                    }
                }
            }

            /// <summary>Gets or sets the identifier of the session.</summary>
            /// <value>The identifier of the session.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            public string SessionId
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.sessionId;
                }

                set
                {
                    this.ThrowIfDisposed();
                    this.CopySessionId(value);
                    this.PartitionKey = value;
                }
            }

            /// <summary>Gets or sets the session identifier to reply to.</summary>
            /// <value>The session identifier to reply to.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            public string ReplyToSessionId
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.replyToSessionId;
                }

                set
                {
                    this.ThrowIfDisposed();
                    BrokeredMessage.ValidateSessionId(value);
                    this.replyToSessionId = value;
                    if (value == null)
                    {
                        this.ClearInitializedMember(MessageMembers.ReplyToSessionId);
                    }
                    else
                    {
                        this.initializedMembers |= MessageMembers.ReplyToSessionId;
                    }
                }
            }

            /// <summary>Gets the number of deliveries.</summary>
            /// <value>The number of deliveries.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            /// <exception cref="System.InvalidOperationException">Thrown if the message has not been delivered by ServiceBus.</exception>
            public int DeliveryCount
            {
                get
                {
                    this.ThrowIfDisposed();
                    this.ThrowIfNotReceived();
                    return this.receiverHeaders.DeliveryCount;
                }

                internal set
                {
                    this.ThrowIfDisposed();
                    this.EnsureReceiverHeaders();
                    this.initializedMembers |= MessageMembers.DeliveryCount;
                    this.receiverHeaders.DeliveryCount = value;
                }
            }

            /// <summary />
            public string DeadLetterSource
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.deadLetterSource;
                }

                internal set
                {
                    this.ThrowIfDisposed();
                    this.deadLetterSource = value;

                    if (string.IsNullOrEmpty(value))
                    {
                        this.ClearInitializedMember(MessageMembers.DeadLetterSource);
                    }
                    else
                    {
                        this.initializedMembers |= MessageMembers.DeadLetterSource;
                    }
                }
            }

            /// <summary>Gets the date and time in UTC at which the message is set to expire.</summary>
            /// <value>The message expiration time in UTC.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            /// <exception cref="System.InvalidOperationException">If the message has not been delivered by ServerBus.</exception>
            public DateTime ExpiresAtUtc
            {
                get
                {
                    this.ThrowIfDisposed();
                    this.ThrowIfNotReceived();
                    if (this.TimeToLive >= DateTime.MaxValue.Subtract(this.enqueuedTimeUtc))
                    {
                        return DateTime.MaxValue;
                    }

                    return this.EnqueuedTimeUtc.Add(this.TimeToLive);
                }
            }

            /// <summary>Gets the date and time in UTC until which the message will be locked in the queue/subscription.</summary>
            /// <value>The date and time until which the message will be locked in the queue/subscription.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            /// <exception cref="System.InvalidOperationException">Thrown if the message was not received from the ServiceBus.</exception>
            public DateTime LockedUntilUtc
            {
                get
                {
                    this.ThrowIfDisposed();

                    // TODO: this.ThrowIfNotLocked();
                    return this.receiverHeaders.LockedUntilUtc;
                }

                internal set
                {
                    this.ThrowIfDisposed();
                    this.EnsureReceiverHeaders();

                    this.initializedMembers |= MessageMembers.LockedUntilUtc;
                    this.receiverHeaders.LockedUntilUtc = value;
                }
            }

            // TODO:Fix expected exception list once CSDMain# 220699 is fixed

            /// <summary>Gets the lock token assigned by Service Bus to this message.</summary>
            /// <value>The lock token assigned by Service Bus to this message.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            /// <exception cref="System.InvalidOperationException">Thrown if the message was not received from the ServiceBus.</exception>
            public Guid LockToken
            {
                get
                {
                    this.ThrowIfDisposed();

                    // TODO: this.ThrowIfNotLocked();
                    return this.receiverHeaders.LockToken;
                }

                internal set
                {
                    this.ThrowIfDisposed();
                    this.EnsureReceiverHeaders();

                    this.receiverHeaders.LockToken = value;
                    if (value != Guid.Empty)
                    {
                        this.initializedMembers |= MessageMembers.LockToken;
                    }
                    else
                    {
                        this.ClearInitializedMember(MessageMembers.LockToken);
                    }
                }
            }

            /// <summary>Gets or sets the identifier of the message. This is a
            /// user-defined value that Service Bus can use to identify duplicate messages, if enabled.</summary>
            /// <value>The identifier of the message.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in a disposed state.</exception>
            /// <exception cref="System.ArgumentException">Thrown if the message identifier is null or exceeds 128 characters in length.</exception>
            public string MessageId
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.messageId;
                }

                set
                {
                    this.ThrowIfDisposed();
                    BrokeredMessage.ValidateMessageId(value);
                    this.initializedMembers |= MessageMembers.MessageId;
                    this.messageId = value;
                }
            }

            ///// <summary> Gets or sets a context for the receive. </summary>
            ///// <value> The receive context. </value>
            ///// TODO
            ////internal ReceiveContext ReceiveContext
            ////{
            ////    get
            ////    {
            ////        this.ThrowIfDisposed();
            ////        return this.receiveContext;
            ////    }

            ////    set
            ////    {
            ////        this.ThrowIfDisposed();
            ////        this.receiveContext = value;
            ////    }
            ////}

            /// <summary>Gets or sets the type of the content.</summary>
            /// <value>The type of the content of the message body. This is a
            /// content type identifier utilized by the sender and receiver for application specific logic.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            public string ContentType
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.contentType;
                }

                set
                {
                    this.ThrowIfDisposed();
                    this.contentType = value;
                    if (value == null)
                    {
                        this.ClearInitializedMember(MessageMembers.ContentType);
                    }
                    else
                    {
                        this.initializedMembers |= MessageMembers.ContentType;
                    }
                }
            }

            /// <summary>Gets or sets a partition key for sending a transactional message to a queue or topic that is not session-aware.</summary>
            /// <value>The partition key for sending a transactional message.</value>
            public string PartitionKey
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.partitionKey;
                }

                set
                {
                    this.ThrowIfDisposed();
                    this.ThrowIfDominatingPropertyIsNotEqualToNonNullDormantProperty(MessageMembers.PartitionKey, MessageMembers.SessionId, value, this.sessionId);
                    this.CopyPartitionKey(value);
                }
            }

            /// <summary>Gets or sets a partition key value when a transaction is to be used to send messages via a transfer queue.</summary>
            /// <value>The partition key value when a transaction is to be used to send messages via a transfer queue.</value>
            public string ViaPartitionKey
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.viaPartitionKey;
                }

                set
                {
                    this.ThrowIfDisposed();
                    BrokeredMessage.ValidatePartitionKey("ViaPartitionKey", value);
                    this.viaPartitionKey = value;
                    if (value == null)
                    {
                        this.ClearInitializedMember(MessageMembers.ViaPartitionKey);
                    }
                    else
                    {
                        this.initializedMembers |= MessageMembers.ViaPartitionKey;
                    }
                }
            }

            /// <summary>Gets or sets the application specific label.</summary>
            /// <value>The application specific label.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            public string Label
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.label;
                }

                set
                {
                    this.ThrowIfDisposed();
                    this.label = value;
                    if (value == null)
                    {
                        this.ClearInitializedMember(MessageMembers.Label);
                    }
                    else
                    {
                        this.initializedMembers |= MessageMembers.Label;
                    }
                }
            }

            /// <summary>Gets the application specific message properties.</summary>
            /// <value>The application specific message properties.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            public IDictionary<string, object> Properties
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.InternalProperties;
                }
            }

            /// <summary>Gets or sets the address of the queue to reply to.</summary>
            /// <value>The reply to queue address.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            public string ReplyTo
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.replyTo;
                }

                set
                {
                    this.ThrowIfDisposed();
                    this.replyTo = value;
                    if (value == null)
                    {
                        this.ClearInitializedMember(MessageMembers.ReplyTo);
                    }
                    else
                    {
                        this.initializedMembers |= MessageMembers.ReplyTo;
                    }
                }
            }

            /// <summary>Gets or sets the date and time of the sent time in UTC.</summary>
            /// <value>The enqueue time in UTC. This value represents the actual time of enqueuing the message.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            public DateTime EnqueuedTimeUtc
            {
                get
                {
                    this.ThrowIfDisposed();
                    this.ThrowIfNotReceived();
                    return this.enqueuedTimeUtc;
                }

                internal set
                {
                    this.ThrowIfDisposed();
                    this.EnsureReceiverHeaders();
                    this.initializedMembers |= MessageMembers.EnqueuedTimeUtc;
                    this.enqueuedTimeUtc = value;
                }
            }

            /// <summary>Gets or sets the date and time in UTC at which the message will be enqueued. This
            /// property returns the time in UTC; when setting the property, the supplied DateTime value must also be in UTC.</summary>
            /// <value>The scheduled enqueue time in UTC. This value is for delayed message sending.
            /// It is utilized to delay messages sending to a specific time in the future.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            /// <exception cref="System.ArgumentOutOfRangeException">Thrown if the passed in value is DateTime.MaxValue.</exception>
            public DateTime ScheduledEnqueueTimeUtc
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.scheduledEnqueueTimeUtc;
                }

                set
                {
                    this.ThrowIfDisposed();

                    if (value == DateTime.MaxValue)
                    {
                        throw new ArgumentOutOfRangeException("ScheduledEnqueueTimeUtc");
                    }

                    this.initializedMembers |= MessageMembers.ScheduledEnqueueTimeUtc;
                    this.scheduledEnqueueTimeUtc = value;
                }
            }

            /// <summary>Gets the unique number assigned to a message by the Service Bus.</summary>
            /// <value>The unique number assigned to a message by the Service Bus.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            /// <exception cref="System.InvalidOperationException">Thrown if the message was not received from the message server.</exception>
            public long SequenceNumber
            {
                get
                {
                    this.ThrowIfDisposed();
                    this.ThrowIfNotReceived();
                    return this.receiverHeaders.SequenceNumber;
                }

                internal set
                {
                    this.ThrowIfDisposed();
                    this.EnsureReceiverHeaders();
                    this.initializedMembers |= MessageMembers.SequenceNumber;
                    this.receiverHeaders.SequenceNumber = value;
                }
            }

            /// <summary>Gets or sets the enqueued sequence number of the message.</summary>
            /// <value>The enqueued sequence number of the message.</value>
            public long EnqueuedSequenceNumber
            {
                get
                {
                    this.ThrowIfDisposed();
                    this.ThrowIfNotReceived();
                    return this.receiverHeaders.EnqueuedSequenceNumber;
                }

                internal set
                {
                    this.ThrowIfDisposed();
                    this.EnsureReceiverHeaders();

                    this.initializedMembers |= MessageMembers.EnqueuedSequenceNumber;
                    this.receiverHeaders.EnqueuedSequenceNumber = value;
                }
            }

            /// <summary>Gets the size of the message in bytes.</summary>
            /// <value>The message size in bytes.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            public long Size
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.HeaderSize + this.BodySize;
                }
            }

            /// <summary>Gets or sets the message’s time to live value. This is the duration after which the message expires, starting from when the message is sent to the Service Bus. Messages older than their TimeToLive value will expire and no longer be retained in the message store. Subscribers will be unable to receive expired messages.TimeToLive is the maximum lifetime that a message can receive, but its value cannot exceed the entity specified the
            /// <see cref="Microsoft.ServiceBus.Messaging.QueueDescription.DefaultMessageTimeToLive" /> value on the destination queue or subscription. If a lower TimeToLive value is specified, it will be applied to the individual message. However, a larger value specified on the message will be overridden by the entity’s DefaultMessageTimeToLive value.</summary>
            /// <value>The message’s time to live value.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            /// <exception cref="System.ArgumentOutOfRangeException">Thrown if the passed in value is less than or equal to TimeSpan.Zero.</exception>
            public TimeSpan TimeToLive
            {
                get
                {
                    this.ThrowIfDisposed();
                    if (this.timeToLive == TimeSpan.Zero)
                    {
                        return TimeSpan.MaxValue;
                    }

                    return this.timeToLive;
                }

                set
                {
                    this.ThrowIfDisposed();
                    this.initializedMembers |= MessageMembers.TimeToLive;
                    this.timeToLive = value;
                }
            }

            /// <summary>Gets or sets the send to address.</summary>
            /// <value>The send to address.</value>
            /// <exception cref="System.ObjectDisposedException">Thrown if the message is in disposed state.</exception>
            public string To
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.to;
                }

                set
                {
                    this.ThrowIfDisposed();
                    this.to = value;
                    if (value == null)
                    {
                        this.ClearInitializedMember(MessageMembers.To);
                    }
                    else
                    {
                        this.initializedMembers |= MessageMembers.To;
                    }
                }
            }

            /// <summary>Specifies whether the message has been consumed.</summary>
            /// <value>true if the message has been consumed; otherwise, false.</value>
            public bool IsBodyConsumed
            {
                get
                {
                    // Body is consumed in 2 cases.
                    // - Someone called GetBody() - this happens in both send and receive.
                    // - Send operation "considered" a message been sent.
                    // Note: this is not a thread safe boolean. IsBodyConsumed == false
                    // can still lead to Send/GetBody() throwing exception saying body is consumed
                    // in a multi-threaded scenario.
                    this.ThrowIfDisposed();
                    return this.getBodyCalled == 1 || this.messageConsumed == 1;
                }
            }

            internal short PartitionId
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.partitionId;
                }

                set
                {
                    if (value < 0)
                    {
                        throw new ArgumentOutOfRangeException("PartitionId");
                    }

                    this.ThrowIfDisposed();
                    this.initializedMembers |= MessageMembers.PartitionId;
                    this.partitionId = value;
                }
            }

            /// <summary> Gets or sets the the Publisher. </summary>
            /// <value> Identifies the Publisher Sending the Message. </value>
            /// <exception cref="ObjectDisposedException">Thrown if message is in disposed state.</exception>
            /// <exception cref="InvalidOperationException">Thrown if <seealso cref="PartitionKey"/> or <seealso cref="SessionId"/> are set to different values.</exception>
            internal string Publisher
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.publisher;
                }

                set
                {
                    this.ThrowIfDisposed();
                    BrokeredMessage.ValidatePartitionKey("Publisher", value);
                    if (value != null)
                    {
                        this.ThrowIfDominatingPropertyIsNotEqualToNonNullDormantProperty(MessageMembers.Publisher, MessageMembers.PartitionKey, value, this.partitionKey);
                    }

                    if (string.IsNullOrEmpty(value))
                    {
                        this.ClearInitializedMember(MessageMembers.Publisher);
                    }
                    else
                    {
                        this.initializedMembers |= MessageMembers.Publisher;
                    }

                    this.publisher = value;
                }
            }

            /// <summary> Gets the size of the message header in bytes. </summary>
            /// <value> The size of the message header. </value>
            /// <exception cref="ObjectDisposedException">Thrown if message is in disposed state</exception>
            internal long HeaderSize
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.headerSize;
                }
            }

            internal long BodySize
            {
                get
                {
                    this.ThrowIfDisposed();
                    if (this.bodyStream != null && this.bodyStream.CanSeek)
                    {
                        this.bodySize = this.bodyStream.Length;
                    }

                    return this.bodySize;
                }
            }

            /// <summary> Gets or sets the body stream. </summary>
            /// <value> The message body stream. </value>
            /// <exception cref="ObjectDisposedException">Thrown if message is in disposed state</exception>
            internal Stream BodyStream
            {
                get
                {
                    this.ThrowIfDisposed();
                    return this.bodyStream;
                }

                set
                {
                    this.ThrowIfDisposed();

                    if (this.bodyStream != null && this.ownsBodyStream)
                    {
                        this.bodyStream.Dispose();
                    }

                    this.bodyStream = value;
                }
            }

            /// <summary> Gets or sets the initialized members. </summary>
            /// <value> The initialized members. </value>
            internal MessageMembers InitializedMembers
            {
                get
                {
                    return this.initializedMembers;
                }

                set
                {
                    this.initializedMembers = value;
                }
            }

            /// <summary> Gets a value indicating whether this object is lock token set. </summary>
            /// <value> true if this object is lock token set, false if not. </value>
            internal bool IsLockTokenSet
            {
                get
                {
                    return (this.initializedMembers & MessageMembers.LockToken) != 0;
                }
            }

            /// <summary> Gets the identifier of the body. </summary>
            /// <value> The identifier of the body. </value>
            internal long BodyId
            {
                get
                {
                    return this.bodyId;
                }

                set
                {
                    this.bodyId = value;
                }
            }

            /// <summary> Gets the internal properties. </summary>
            /// <value> The internal properties. </value>
            internal IDictionary<string, object> InternalProperties
            {
                get
                {
                    if (this.properties == null)
                    {
                        Interlocked.CompareExchange(ref this.properties, new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase), null);
                    }

                    return this.properties;
                }
            }

            /// <summary>
            /// Indicate if the BorkeredMessage has been accessed (or marked) as consumed.
            /// </summary>
            /// <remarks>Initially IsConsumed is false. First read of this property will also
            /// marked the IsConsumed to be true (atomically). Subsequence will be false.
            ///
            /// Also note that this does not take transaction into account, so if a message
            /// is marked as consumed but then transaction is aborted, this property will
            /// continue to indicate false.</remarks>
            internal bool IsConsumed
            {
                get
                {
                    // First get will be 0, with subsequence get being 1 (true)
                    return Interlocked.Exchange(ref this.messageConsumed, 1) == 1;
                }

                set
                {
                    int intValue = value ? 1 : 0;
                    Interlocked.Exchange(ref this.messageConsumed, intValue);
                }
            }

            /// <summary>Specify generator to be used to generate BrokeredMessage.MessageId value.
            /// <param name="messageIdGenerator">Message ID generator.</param>
            /// <remarks>Be default, no value is assigned.</remarks>
            /// </summary>
            /// <exception cref="ArgumentNullException">Thrown if invoked with null.</exception>
            public static void SetMessageIdGenerator(Func<string> messageIdGenerator)
            {
                messageIdGeneratorFunc = messageIdGenerator;
            }

            /// <summary>Deserializes the brokered message body into an object of the specified type by using the
            /// <see cref="System.Runtime.Serialization.DataContractSerializer" /> with a binary
            /// <see cref="System.Xml.XmlDictionaryReader" />.</summary>
            /// <typeparam name="T">The type to which the message body will be deserialized.</typeparam>
            /// <returns>The deserialized object or graph.</returns>
            /// <exception cref="System.ObjectDisposedException">If the message is in disposed state or the message body stream is already disposed.</exception>
            /// <exception cref="System.InvalidOperationException">If the message contains a null body stream or the
            /// body stream contains no data or the message body has already been consumed.</exception>
            public T GetBody<T>()
            {
                if (typeof(T) == typeof(Stream))
                {
                    this.SetGetBodyCalled();
                    return (T)(object)this.BodyStream;
                }

                if (this.bodyObjectDecoded && this.bodyObject != null)
                {
                    this.SetGetBodyCalled();
                    return (T)this.bodyObject;
                }

                return this.GetBody<T>(new DataContractBinarySerializer(typeof(T)));
            }

            /// <summary>Deserializes the BrokeredMessage body into an object of the specified type using
            /// DataContractSerializer with a Binary XmlObjectSerializer. </summary>
            /// <typeparam name="T"> Generic type parameter. </typeparam>
            /// <param name="serializer"> The serializer object. </param>
            /// <returns> The deserialized object/graph</returns>
            /// <exception cref="ObjectDisposedException"> Thrown if the message is in disposed state. </exception>
            /// <exception cref="ArgumentNullException"> Thrown when invoked with a Null serializer object. </exception>
            /// <exception cref="InvalidOperationException"> Thrown if the message contains a Null body stream, contains no data,
            /// or if the stream has been read once (through any GetBody() calls). </exception>
            public T GetBody<T>(XmlObjectSerializer serializer)
            {
                if (serializer == null)
                {
                    // TODO: throw FxTrace.Exception.AsError(new ArgumentNullException("serializer"));
                    throw new ArgumentNullException(nameof(serializer));
                }

                this.ThrowIfDisposed();
                this.SetGetBodyCalled();

                if (this.BodyStream == null)
                {
                    // TODO: Should use IsValueType??
                    if (typeof(T) == typeof(ValueType))
                    {
                        throw new InvalidOperationException("MessageBodyNull");
                    }

                    return default(T);
                }

                if (this.BodyStream.CanSeek)
                {
                    if (this.BodyStream.Length == 0)
                    {
                        // There are 2 cases where there is a stream in the first place:
                        // (a) user called Message.CreateMessage(object/stream)
                        // (b) internal code set Message.BodyStream to non-null stream
                        // Either case, someone now force stream to be empty. We should always throw
                        // in these cases.
                        throw new InvalidOperationException("MessageBodyNull");
                    }

                    this.BodyStream.Position = 0;
                }

                return (T)serializer.ReadObject(this.BodyStream);
            }

            /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
            public void Dispose()
            {
                this.Dispose(true);
            }

            /// <summary>Returns a string that represents the current message.</summary>
            /// <returns>The string representation of the current message.</returns>
            public override string ToString()
            {
                return string.Format(CultureInfo.CurrentCulture, "{0}{{MessageId:{1}}}", base.ToString(), this.MessageId);
            }

            /// <summary>Clones a message, so that it is possible to send a clone of a message as a new message.</summary>
            /// <returns>The <see cref="BrokeredMessage" /> that contains the cloned message.</returns>
            [System.Diagnostics.CodeAnalysis.SuppressMessage(
                "Microsoft.Usage",
                "CA2202:Do not dispose objects multiple times",
                Justification = "Safe here. Any future behavior change is easy to detect")]
            public BrokeredMessage Clone()
            {
                this.ThrowIfDisposed();

                return new BrokeredMessage(this, clientSideCloning: true);
            }

            internal static IEnumerable<IDisposable> CloneDisposables(IEnumerable<IDisposable> disposables)
            {
                // clone the disposables if they support it
                if (disposables == null)
                {
                    return null;
                }

                List<IDisposable> clonedDisposables = new List<IDisposable>();
                foreach (IDisposable obj in disposables)
                {
                    ICloneable cloneable = obj as ICloneable;
                    if (cloneable != null)
                    {
                        object clone = cloneable.Clone();
                        clonedDisposables.Add((IDisposable)clone);
                    }
                }
                return clonedDisposables.Count > 0 ? clonedDisposables : null;
            }

            internal object ClearBodyObject()
            {
                object obj = this.bodyObject;
                this.bodyObject = null;
                return obj;
            }

            internal void ClearPartitionId()
            {
                this.partitionId = default(short);
                this.ClearInitializedMember(MessageMembers.PartitionId);
            }

            /// <summary>
            /// Attached an IDisposable object to the BrokeredMessage which should be disposed when the message itself is disposed
            /// </summary>
            /// <param name="disposables"></param>
            internal void AttachDisposables(IEnumerable<IDisposable> disposables)
            {
                if (disposables == null)
                {
                    return;
                }

                if (this.attachedDisposables == null)
                {
                    lock (this.disposablesSyncObject)
                    {
                        if (this.attachedDisposables == null)
                        {
                            this.attachedDisposables = new List<IDisposable>(4);
                        }

                        this.attachedDisposables.AddRange(disposables);
                        return;
                    }
                }

                lock (this.disposablesSyncObject)
                {
                    this.attachedDisposables.AddRange(disposables);
                }
            }

            internal void CopySessionId(string sessionId)
            {
                BrokeredMessage.ValidateSessionId(sessionId);
                this.sessionId = sessionId;
                if (sessionId == null)
                {
                    this.ClearInitializedMember(MessageMembers.SessionId);
                }
                else
                {
                    this.initializedMembers |= MessageMembers.SessionId;
                }
            }

            internal void CopyPartitionKey(string partitionKey)
            {
                BrokeredMessage.ValidatePartitionKey("PartitionKey", partitionKey);
                this.partitionKey = partitionKey;
                if (partitionKey == null)
                {
                    this.ClearInitializedMember(MessageMembers.PartitionKey);
                }
                else
                {
                    this.initializedMembers |= MessageMembers.PartitionKey;
                }
            }

            internal bool IsMembersSet(MessageMembers members)
            {
                bool membersSet = ((this.InitializedMembers & members) != 0);

                return membersSet;
            }

            static Stream CloneStream(Stream originalStream, bool canThrowException = false)
            {
                Stream clonedStream = null;

                if (originalStream != null)
                {
                    MemoryStream memoryStream;
                    ICloneable cloneable;

                    if ((memoryStream = originalStream as MemoryStream) != null)
                    {
                        // Note: memoryStream.GetBuffer() doesn't work
                        clonedStream = new MemoryStream(memoryStream.ToArray(), 0, (int)memoryStream.Length, false, true);
                    }
                    else if ((cloneable = originalStream as ICloneable) != null)
                    {
                        clonedStream = (Stream)cloneable.Clone();
                    }
                    else if (canThrowException)
                    {
                        // TODO: throw Fx.Exception.AsError(new InvalidOperationException(SRClient.BrokeredMessageStreamNotCloneable(originalStream.GetType().FullName)));
                        throw new InvalidOperationException("BrokeredMessageStreamNotCloneable");
                    }
                }

                return clonedStream;
            }

            /// <summary> Validate message identifier. </summary>
            /// <exception cref="ArgumentException">
            /// Thrown when messageId is null, or empty or greater than the maximum message length.
            /// </exception>
            /// <param name="messageId"> Identifier for the message. </param>
            static void ValidateMessageId(string messageId)
            {
                if (string.IsNullOrEmpty(messageId) ||
                    messageId.Length > Constants.MaxMessageIdLength)
                {
                    // TODO: throw FxTrace.Exception.Argument("messageId", SRClient.MessageIdIsNullOrEmptyOrOverMaxValue(Constants.MaxMessageIdLength));
                    throw new ArgumentException("MessageIdIsNullOrEmptyOrOverMaxValue");
                }
            }

            /// <summary> Validate session identifier. </summary>
            /// <exception cref="ArgumentException">
            /// Thrown when sessionId is greater than the maximum session ID length.
            /// </exception>
            /// <param name="sessionId"> Identifier for the session. </param>
            static void ValidateSessionId(string sessionId)
            {
                if (sessionId != null && sessionId.Length > Constants.MaxSessionIdLength)
                {
                    // TODO: throw FxTrace.Exception.Argument("sessionId", SRClient.SessionIdIsOverMaxValue(Constants.MaxSessionIdLength));
                    throw new ArgumentException("SessionIdIsOverMaxValue");
                }
            }

            static void ValidatePartitionKey(string partitionKeyPropertyName, string partitionKey)
            {
                if (partitionKey != null && partitionKey.Length > Constants.MaxPartitionKeyLength)
                {
                    // TODO: throw FxTrace.Exception.Argument(partitionKeyPropertyName, SRClient.PropertyOverMaxValue(partitionKeyPropertyName, Constants.MaxPartitionKeyLength));
                    throw new ArgumentException("PropertyValueOverMaxValue");
                }
            }

            static Type GetObjectType(object value)
            {
                return (value == null) ? typeof(object) : value.GetType();
            }

            /// <summary> Performs application-defined tasks associated with freeing, releasing, or resetting
            /// unmanaged resources. </summary>
            /// <param name="disposing"> true if resources should be disposed, false if not. </param>
            void Dispose(bool disposing)
            {
                if (!this.disposed)
                {
                    if (disposing)
                    {
                        if (this.BodyStream != null && this.ownsBodyStream)
                        {
                            this.BodyStream.Dispose();
                            this.BodyStream = null;
                        }

                        this.bodyObject = null;

                        if (this.attachedDisposables != null)
                        {
                            foreach (IDisposable disposable in this.attachedDisposables)
                            {
                                disposable.Dispose();
                            }
                        }
                    }

                    this.disposed = true;
                }
            }

            void ClearInitializedMember(MessageMembers memberToClear)
            {
                this.initializedMembers &= ~memberToClear;
            }

            void SetGetBodyCalled()
            {
                if (Interlocked.Exchange(ref this.getBodyCalled, 1) == 1)
                {
                    // TODO: throw Fx.Exception.AsError(new InvalidOperationException(SRClient.MessageBodyConsumed));
                    throw new InvalidOperationException("MessageBodyConsumed");
                }
            }

            /// <summary> Copies the message headers described by originalMessage. </summary>
            /// <param name="originalMessage"> Message describing the original. </param>
            /// <param name="clientSideCloning"> specific if it is a client side initialized code path.</param>
            void CopyMessageHeaders(BrokeredMessage originalMessage, bool clientSideCloning = false)
            {
                this.MessageId = originalMessage.MessageId;
                this.headerSize = originalMessage.HeaderSize;

                if ((originalMessage.InitializedMembers & MessageMembers.SessionId) != 0)
                {
                    this.CopySessionId(originalMessage.SessionId);
                }

                if ((originalMessage.initializedMembers & MessageMembers.PartitionKey) != 0)
                {
                    this.CopyPartitionKey(originalMessage.PartitionKey);
                }

                if ((originalMessage.initializedMembers & MessageMembers.ViaPartitionKey) != 0)
                {
                    this.ViaPartitionKey = originalMessage.ViaPartitionKey;
                }

                if ((originalMessage.InitializedMembers & MessageMembers.ScheduledEnqueueTimeUtc) != 0)
                {
                    this.ScheduledEnqueueTimeUtc = originalMessage.ScheduledEnqueueTimeUtc;
                }

                if ((originalMessage.InitializedMembers & MessageMembers.TimeToLive) != 0)
                {
                    this.TimeToLive = originalMessage.TimeToLive;
                }

                string originalMessageLabel = originalMessage.Label;
                if (originalMessageLabel != null)
                {
                    this.Label = originalMessageLabel;
                }

                if (originalMessage.CorrelationId != null)
                {
                    this.CorrelationId = originalMessage.CorrelationId;
                }

                if (originalMessage.ReplyTo != null)
                {
                    this.ReplyTo = originalMessage.ReplyTo;
                }

                if (originalMessage.To != null)
                {
                    this.To = originalMessage.To;
                }

                if ((originalMessage.InitializedMembers & MessageMembers.ReplyToSessionId) != 0)
                {
                    this.ReplyToSessionId = originalMessage.ReplyToSessionId;
                }

                if (originalMessage.ContentType != null)
                {
                    this.ContentType = originalMessage.ContentType;
                }

                foreach (KeyValuePair<string, object> property in originalMessage.Properties)
                {
                    this.InternalProperties.Add(property);
                }

                // Publisher property is intended to be made public eventually.
                // So it gets cloned even in client side cloning.
                if (originalMessage.Publisher != null)
                {
                    this.Publisher = originalMessage.Publisher;
                }

                // Copy any internal properties (which cannot be set through public API)
                // only when it's not client side cloning (which is invoked using public API).
                if (!clientSideCloning)
                {
                    if ((originalMessage.InitializedMembers & MessageMembers.PartitionId) != 0)
                    {
                        this.PartitionId = originalMessage.PartitionId;
                    }

                    ReceiverHeaders originalMessageReceiverHeaders = originalMessage.receiverHeaders;
                    if (originalMessageReceiverHeaders != null)
                    {
                        // Don't copy LockToken or LockedUntilUtc
                        this.BodyId = originalMessage.BodyId;
                        this.DeliveryCount = originalMessageReceiverHeaders.DeliveryCount;
                        this.SequenceNumber = originalMessageReceiverHeaders.SequenceNumber;
                        this.EnqueuedTimeUtc = originalMessage.EnqueuedTimeUtc;
                        this.EnqueuedSequenceNumber = originalMessageReceiverHeaders.EnqueuedSequenceNumber;
                    }

                    if ((originalMessage.initializedMembers & MessageMembers.DeadLetterSource) != 0)
                    {
                        this.DeadLetterSource = originalMessage.DeadLetterSource;
                    }
                }
            }

            /// <summary> Ensures that receiver headers. </summary>
            void EnsureReceiverHeaders()
            {
                if (this.receiverHeaders == null)
                {
                    this.receiverHeaders = new ReceiverHeaders();
                }
            }

            /// <summary> Throw if disposed. </summary>
            /// <exception cref="Fx.Exception"> Thrown when object disposed. </exception>
            void ThrowIfDisposed()
            {
                if (this.disposed)
                {
                    // TODO: throw Fx.Exception.ObjectDisposed("BrokeredMessage has been disposed.");
                    throw new ObjectDisposedException("BrokeredMessage has been disposed.");
                }
            }

            /// <summary> Throw if not locked. </summary>
            /// <exception cref="Exception"> Thrown when as error. </exception>
            void ThrowIfNotLocked()
            {
            }

            /// <summary> Throw if not received. </summary>
            /// <exception cref="Fx.Exception"> Thrown when as error. </exception>
            void ThrowIfNotReceived()
            {
                if (this.receiverHeaders == null)
                {
                    throw new InvalidOperationException();
                }
            }

            void ThrowIfDominatingPropertyIsNotEqualToNonNullDormantProperty(MessageMembers dominatingProperty, MessageMembers dormantProperty, string dominatingPropsValue, string dormantPropsValue)
            {
                if ((this.initializedMembers & dormantProperty) != 0 && !string.Equals(dominatingPropsValue, dormantPropsValue))
                {
                    // TODO: throw FxTrace.Exception.AsError(new InvalidOperationException(SRClient.DominatingPropertyMustBeEqualsToNonNullDormantProperty(dominatingProperty, dormantProperty)));
                    throw new InvalidOperationException("DominatingPropertyMustBeEqualsToNonNullDormantProperty");
                }
            }

            /// <summary> Receiver headers. </summary>
            internal sealed class ReceiverHeaders
            {
                /// <summary> Gets or sets the number of deliveries. </summary>
                /// <value> The number of deliveries. </value>
                public int DeliveryCount { get; set; }

                /// <summary> Gets or sets the Date/Time of the locked until utc. </summary>
                /// <value> The locked until utc. </value>
                public DateTime LockedUntilUtc { get; set; }

                /// <summary> Gets or sets the lock token. </summary>
                /// <value> The lock token. </value>
                public Guid LockToken { get; set; }

                /// <summary> Gets or sets the sequence number. </summary>
                /// <value> The sequence number. </value>
                public long SequenceNumber { get; set; }

                /// <summary> Gets or sets the enqueued sequence number. </summary>
                /// <value> The Enqueued sequence number. </value>
                public long EnqueuedSequenceNumber { get; set; }
            }
        }

        static class Constants
        {
            public const int MaxMessageIdLength = 128;

            public const int MaxDestinationLength = 128;

            public const int MaxPartitionKeyLength = 128;

            public const int MaxSessionIdLength = 128;

            public static readonly int MaximumMessageHeaderPropertySize = ushort.MaxValue;
        }

        public enum ReceiveMode
        {
            /// <summary>Specifies the PeekLock receive mode. This is the default value for <see cref="ReceiveMode" />.</summary>
            PeekLock,

            /// <summary>Specifies the ReceiveAndDelete receive mode.</summary>
            ReceiveAndDelete
        }
    }
}
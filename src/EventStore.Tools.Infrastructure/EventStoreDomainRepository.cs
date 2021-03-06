using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EventStore.ClientAPI;
using Newtonsoft.Json;

namespace EventStore.Tools.Infrastructure
{
    public class EventStoreDomainRepository : DomainRepositoryBase
    {
        public readonly string Category;
        private readonly IEventStoreConnection _connection;
        private readonly int _expectedVersion;
        private readonly StreamMetadata _metadata;

        public EventStoreDomainRepository(string category, IEventStoreConnection connection, StreamMetadata metadata, int? expectedMetastreamVersion = null)
        {
            Category = category;
            _connection = connection;
            _expectedVersion = expectedMetastreamVersion ?? ExpectedVersion.Any;
            _metadata = metadata;
        }

        public EventStoreDomainRepository(string category, IEventStoreConnection connection) : this(category, connection, null)
        {
        }

        private string AggregateToStreamName(Type type, string id)
        {
            return $"{Category}-{type.Name}-{id}";
        }

        public override TResult GetById<TResult>(string id) 
        {
            var streamName = AggregateToStreamName(typeof(TResult), id);
            var eventsSlice = _connection.ReadStreamEventsForwardAsync(streamName, 0, 4096, false);
            if (eventsSlice.Result.Status == SliceReadStatus.StreamNotFound)
            {
                throw new AggregateNotFoundException("Could not found aggregate of type " + typeof(TResult) + " and id " + id);
            }
            var deserializedEvents = eventsSlice.Result.Events.Select(e =>
            {
                var metadata = SerializationUtils.DeserializeObject<Dictionary<string, string>>(e.OriginalEvent.Metadata);
                var eventData = SerializationUtils.DeserializeObject(e.OriginalEvent.Data, metadata[SerializationUtils.EventClrTypeHeader]);
                return eventData as IEvent;
            });
            return BuildAggregate<TResult>(deserializedEvents);
        }

        public EventData CreateEventData(object @event)
        {
            var eventHeaders = new Dictionary<string, string>()
            {
                {
                    SerializationUtils.EventClrTypeHeader, @event.GetType().AssemblyQualifiedName
                },
                {
                    "Domain", Category
                }
            };
            var eventDataHeaders = SerializeObject(eventHeaders);
            var data = SerializeObject(@event);
            var eventData = new EventData(Guid.NewGuid(), @event.GetType().Name, true, data, eventDataHeaders);
            return eventData;
        }

        private static byte[] SerializeObject(object obj)
        {
            var jsonObj = JsonConvert.SerializeObject(obj);
            var data = Encoding.UTF8.GetBytes(jsonObj);
            return data;
        }

        public override IEnumerable<IEvent> Save<TAggregate>(TAggregate aggregate)
        {
            var streamName = AggregateToStreamName(aggregate.GetType(), aggregate.AggregateId);
            SaveMetadata(streamName);
            var events = aggregate.UncommitedEvents().ToList();
            var originalVersion = CalculateExpectedVersion(aggregate, events);
            var expectedVersion = originalVersion == 0 ? ExpectedVersion.NoStream : originalVersion - 1;
            var eventData = events.Select(CreateEventData).ToArray();
            try
            {
                if (events.Count > 0)
                    _connection.AppendToStreamAsync(streamName, expectedVersion, eventData).Wait();
            }
            catch (AggregateException)
            {
                // Try to save using ExpectedVersion.Any to swallow silently WrongExpectedVersion error
                _connection.AppendToStreamAsync(streamName, ExpectedVersion.Any, eventData).Wait();
            }
            aggregate.ClearUncommitedEvents();
            return events;
        }

        public override Task<WriteResult> SaveAsync<TAggregate>(TAggregate aggregate)
        {
            var streamName = AggregateToStreamName(aggregate.GetType(), aggregate.AggregateId);
            SaveMetadata(streamName);
            var events = aggregate.UncommitedEvents().ToList();
            var originalVersion = CalculateExpectedVersion(aggregate, events);
            var expectedVersion = originalVersion == -1 ? ExpectedVersion.NoStream : originalVersion;
            var eventData = events.Select(CreateEventData);
            return events.Count > 0 ? _connection.AppendToStreamAsync(streamName, expectedVersion, eventData) : null;
        }

        private void SaveMetadata(string streamName)
        {
            if (_metadata != null)
                // Setting metadata for that specific stream asynchronously to avoid a blocking call
                _connection.SetStreamMetadataAsync(streamName, _expectedVersion, _metadata);
        }
    }
}
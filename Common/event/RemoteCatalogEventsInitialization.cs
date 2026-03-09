using EPiServer.Events;
using EPiServer.Events.Clients;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using Mediachase.Commerce.Catalog.Events;

namespace YourNamespace.Initialization
{
    [InitializableModule]
    [ModuleDependency(typeof(EPiServer.Commerce.Initialization.InitializationModule))]
    public class RemoteCatalogEventsInitialization : IInitializableModule
    {
        public void Initialize(InitializationEngine context)
        {
            // Subscribe to the cross-instance remote event
            Event.Get(CatalogEventBroadcaster.CommerceProductUpdated).Raised += RemoteCatalogEvent_Raised;
        }

        private void RemoteCatalogEvent_Raised(object sender, EventNotificationEventArgs e)
        {
            // Make sure the event param is a CatalogContentUpdateEventArgs
            if (e.Param is CatalogContentUpdateEventArgs updateArgs)
            {
                // Check what type of catalog event this is
                if (updateArgs.EventType == CatalogEventBroadcaster.CatalogEntryUpdatedEventType)
                {
                    // Loop through the entries that were updated
                    if (updateArgs.CatalogEntryIds != null)
                    {
                        foreach (var entryId in updateArgs.CatalogEntryIds)
                        {
                            // Do something with the updated entry
                            // (e.g. invalidate custom caches on this specific web app instance)
                        }
                    }
                }
                else if (updateArgs.EventType == CatalogEventBroadcaster.CatalogEntryDeletedEventType)
                {
                    // Handle deletions
                }
            }
        }

        public void Uninitialize(InitializationEngine context)
        {
            // Always unsubscribe to prevent memory leaks
            Event.Get(CatalogEventBroadcaster.CommerceProductUpdated).Raised -= RemoteCatalogEvent_Raised;
        }
    }
}

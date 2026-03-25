using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using EPiServer;
using EPiServer.Core;
using EPiServer.Events.Clients;
using EPiServer.Framework;
using EPiServer.Framework.Initialization;
using EPiServer.ServiceLocation;
using Mediachase.Commerce.Catalog;
using Mediachase.Commerce.Catalog.Events;
using Mediachase.Commerce.Engine.Events;

// Source: https://github.com/harinarayanann/Com14Training/blob/7a8b4521f6297a6f3bf1182493e4bad677951d12/src/Foundation/Infrastructure/EventsDemoInitializationModule.cs#L34
namespace Foundation.Infrastructure
{
    [InitializableModule]
    [ModuleDependency(typeof(EPiServer.Web.InitializationModule))]
    public class EventsDemoInitializationModule : IInitializableModule
    {
        IContentEvents contentEv;
        ICatalogEvents catalogEv;
        Event evListener;
        Event evKeyListener;
        public void Initialize(InitializationEngine context)
        {
            contentEv = context.Locate.ContentEvents();
            contentEv.PublishedContent += ContentEv_PublishedContent;

            catalogEv = ServiceLocator.Current.GetInstance<ICatalogEvents>();
            catalogEv.EntryUpdated += CatalogEv_EntryUpdated;

            evListener = Event.Get(CatalogEventBroadcaster.CommerceProductUpdated);
            evListener.Raised += EvListner_Raised;

            evKeyListener = Event.Get(CatalogKeyEventBroadcaster.CatalogKeyEventGuid);
            evKeyListener.Raised += EvKeyListener_Raised;
        }

        private void EvKeyListener_Raised(object sender, EPiServer.Events.EventNotificationEventArgs e)
        {
            var eventArgs = (CatalogKeyEventArgs)DeSerialize((byte[])e.Param);
            var priceUpdatedEventArgs = eventArgs as PriceUpdateEventArgs;
            var inventoryUpdatedEventArgs = eventArgs as InventoryUpdateEventArgs;
            var info = new List<string>();
            if (priceUpdatedEventArgs != null)
            {
                info.Add("The price was changed!");
            }
            if (inventoryUpdatedEventArgs != null)
            {
                info.Add("Inventory was changed!");
            }
            WriteToTextFile(info);
        }

        private void EvListner_Raised(object sender, EPiServer.Events.EventNotificationEventArgs e)
        {
            var eventArgs = (CatalogContentUpdateEventArgs)DeSerialize((byte[])e.Param);

            if (eventArgs.EventType == CatalogEventBroadcaster.CatalogEntryUpdatedEventType)
            {
                int entryId = eventArgs.CatalogEntryIds.First();
                ReferenceConverter refConvert = ServiceLocator.Current.GetInstance<ReferenceConverter>();
                ContentReference catRef = refConvert.GetEntryContentLink(entryId);
                IContentLoader loader = ServiceLocator.Current.GetInstance<IContentLoader>();
                var catEntry = loader.Get<IContent>(catRef);

                var info = new List<string>
                    {
                        "Remote Catalog Event Fired!",
                        $"The name of the item updated: {catEntry.Name}"
                    };
                WriteToTextFile(info);
            }

        }

        private void CatalogEv_EntryUpdated(object sender, EntryEventArgs e)
        {
            if (e.Changes.Any())
            {
                EntryChange chg = e.Changes?.First();
                ReferenceConverter refConvert = ServiceLocator.Current.GetInstance<ReferenceConverter>();
                ContentReference catRef = refConvert.GetEntryContentLink(chg.EntryId);
                IContentLoader loader = ServiceLocator.Current.GetInstance<IContentLoader>();
                var catEntry = loader.Get<IContent>(catRef);

                var info = new List<string>
                {
                    "Entry Updated Catalog Event Fired!",
                    $"The name of the item updated: {catEntry.Name}"
                };
                WriteToTextFile(info);
            }
        }

        private void ContentEv_PublishedContent(object sender, EPiServer.ContentEventArgs e)
        {
            var info = new List<string>
                {
                    "Published Content Event Fired!",
                    $"The name of the item published: {e.Content.Name}"
                };
            WriteToTextFile(info);

        }

        public void Uninitialize(InitializationEngine context)
        {
            contentEv.PublishedContent -= ContentEv_PublishedContent;
            catalogEv.EntryUpdated -= CatalogEv_EntryUpdated;
            evListener.Raised -= EvListner_Raised;
            evKeyListener.Raised -= EvKeyListener_Raised;
        }
        private void WriteToTextFile(List<string> lines)
        {
            string mydocPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (lines != null && lines.Count > 0)
            {
                using (StreamWriter outputFile =
                    new StreamWriter(Path.Combine(mydocPath, "EventsDemo.txt"), true))
                {
                    outputFile.WriteLine($"Time of event: {DateTime.Now.ToLongTimeString()}");
                    foreach (string line in lines)
                    {
                        outputFile.WriteLine(line);
                    }
                    outputFile.WriteLine();
                }
            }

        }

        private EventArgs DeSerialize(byte[] buffer)
        {
            var formatter = new BinaryFormatter();
            var stream = new MemoryStream(buffer);
            return formatter.Deserialize(stream) as EventArgs;
        }

    }
}

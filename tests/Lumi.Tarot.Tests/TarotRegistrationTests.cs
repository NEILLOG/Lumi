using Lumi.Core.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Lumi.Tarot.Tests;

/// <summary>簡易的 IServiceCollection 替身（List 本身就實作 IServiceCollection），不需要額外的 DI 套件參考。</summary>
internal sealed class RecordingServices : List<ServiceDescriptor>, IServiceCollection;

public sealed class TarotRegistrationTests
{
    [Fact]
    public void AddLumiTarot_RegistersTheTwoToolsAndTheDeck()
    {
        var services = new RecordingServices();

        services.AddLumiTarot();

        var toolTypes = services.Where(d => d.ServiceType == typeof(ITool)).Select(d => d.ImplementationType!).ToHashSet();
        Assert.Equal(new HashSet<Type> { typeof(TarotDrawTool), typeof(TarotCardTool) }, toolTypes);

        var deck = Assert.Single(services, d => d.ServiceType == typeof(TarotDeck));
        Assert.IsType<TarotDeck>(deck.ImplementationInstance);
        Assert.Equal(ServiceLifetime.Singleton, deck.Lifetime);
    }

    [Fact]
    public void AddLumiTarot_ToolsPassRegistryValidation()
    {
        var services = new RecordingServices();
        services.AddLumiTarot();
        var deck = (TarotDeck)services.Single(d => d.ServiceType == typeof(TarotDeck)).ImplementationInstance!;

        var registry = new ToolRegistry([new TarotDrawTool(deck), new TarotCardTool(deck)]);

        Assert.Equal(["tarot_card", "tarot_draw"], registry.GetAll().Select(t => t.Descriptor.Name));
        Assert.All(registry.GetAll(), t => Assert.Equal(ToolGroups.Tarot, t.Descriptor.Group));
    }

    [Fact]
    public void Deck_LoadsEmbeddedDataOnFirstUse()
    {
        var services = new RecordingServices();
        services.AddLumiTarot();
        var deck = (TarotDeck)services.Single(d => d.ServiceType == typeof(TarotDeck)).ImplementationInstance!;

        Assert.Equal(78, deck.Deck.Cards.Count);
        Assert.Equal(3, deck.Deck.Spreads.Count);
    }
}

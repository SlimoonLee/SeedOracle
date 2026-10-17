using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.TestSupport;
using MegaCrit.Sts2.Core.Rewards;
using SeedOracle.Integration;

namespace SeedOracle.Forecasting;

// The reference branch explicitly skips acquisition rewards. Only a proven
// equivalent card/skip domain may turn that condition into "any choice".
internal sealed class MerchantRewardSelector(PlanningPredictionService.State state, int floor,
    bool nextIsFixedCombat, bool chinese) : ICardSelector
{
    private readonly List<ForecastChoiceFact> facts = [];
    internal IReadOnlyList<ForecastChoiceFact> Facts => facts.Select(fact => fact with
        { GroupCount = facts.Count }).ToArray();

    internal async Task HandleRewardsAsync(RewardsSet rewards)
    {
        await rewards.GenerateWithoutOffering();
        var offered = ShadowIsolation.EnumerateRewards(rewards.Rewards).ToArray();
        if (offered.Any(reward => reward is not CardReward))
            throw new InvalidOperationException("购买附带药水或其他奖励，需要指定领取选择；该分支尚未计算。 / Non-card purchase rewards require explicit selections.");
        foreach (var reward in offered)
            if (!await reward.SelectUnsynchronized()) reward.OnSkipped();
    }

    public Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect) =>
        throw new InvalidOperationException("购买效果需要指定变牌、复制或删牌目标；该分支尚未计算。 / Purchase needs explicit deck targets.");

    public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options,
        IReadOnlyList<CardRewardAlternative> alternatives)
    {
        var skip = alternatives.FirstOrDefault(option => option.OptionId.Equals("Skip", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("新增卡牌奖励不允许跳过，必须指定选择。 / This purchase reward requires a selection.");
        var group = facts.Count;
        var cards = options.Select(option => ForecastItemDetails.Card(option.Card)).ToArray();
        var values = cards.Select((card, index) => "card:" + index + ":" + ForecastContentKey.Item(card)).ToArray();
        var domain = values.Concat(alternatives.Select(option => "alt:" + option.OptionId.ToLowerInvariant())).ToArray();
        facts.Add(new("shop-pickup:" + floor + ":" + group,
            chinese ? $"{floor}层购买所得第{group + 1}组选牌" : $"{floor}F purchase reward {group + 1}",
            "alt:skip", chinese ? "跳过" : "skip", domain, Closed: true,
            Kind: ForecastChoiceKind.CardReward, Floor: floor, Group: group)
        {
            EquivalentValues = ShopPurchaseChoices.CanIgnoreAddedCards(state, nextIsFixedCombat, cards)
                ? values.Append("alt:skip").ToArray() : []
        });
        return new CardRewardSelection { alternative = skip };
    }
}

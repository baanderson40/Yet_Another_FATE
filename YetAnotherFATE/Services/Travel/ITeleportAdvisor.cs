using YetAnotherFATE.Data.Fates;
using YetAnotherFATE.Data.Travel;

namespace YetAnotherFATE.Services.Travel;

public interface ITeleportAdvisor
{
    TeleportRecommendation Evaluate(ActiveFate fate);
    TeleportAnalysis Analyze(ActiveFate fate);
}

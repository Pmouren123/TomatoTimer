using TomatoTimerPro.RuntimeStore.States.Planning;

namespace TomatoTimerPro.Contracts.Storage;

public interface IPlanStorage
{
    Task<PlanState> LoadPlansAsync();
    Task SavePlansAsync(PlanState plans);
}

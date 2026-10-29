using TomatoTimerPro.RuntimeStore.States.Planning;

namespace TomatoTimerPro.Controllers.Components.Planning;

public sealed record PlanStatusChoice(PlanStatus Value, string Label)
{
    public override string ToString() => Label;
    public static IReadOnlyList<PlanStatusChoice> All { get; } =
    [new(PlanStatus.Incomplete, "未完成"), new(PlanStatus.Completed, "已完成"), new(PlanStatus.OnHold, "搁置"), new(PlanStatus.Abandoned, "放弃")];
}

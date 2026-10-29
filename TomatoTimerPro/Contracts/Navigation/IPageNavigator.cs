namespace TomatoTimerPro.Contracts.Navigation;

public interface IPageNavigator
{
    Task<bool> NavigateAsync(string pageId);
}

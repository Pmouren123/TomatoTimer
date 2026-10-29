# WPF MMVC 开发规则

适用项目：TomatoTimerPro（.NET 10 / WPF）。后续新增、修改和重构代码时遵守本规则。

本文是架构与目录约定。目录树中的类型是职责与命名示例，并不代表已经实现；按实际需求创建文件，不预先生成空类或空接口。

## 1. 架构定义

本项目采用 MMVC，不将其替换为 MVC、MVVM，也不额外引入与 Controller 职责重复的 ViewModel 层。

| 层 | 含义 | 固定目录 | 核心职责 |
| --- | --- | --- | --- |
| 第一个 M | 自行实现的运行时缓存仓库 | `TomatoTimerPro/RuntimeStore/` | 保存、读取、更新和通知运行期间共享的业务状态 |
| 第二个 M | Manager，简称 Mgr | `TomatoTimerPro/Managers/` | 管理软件生命周期、页面生命周期、导航和作用域 |
| V | View | `TomatoTimerPro/Views/` | XAML 布局、样式引用、数据绑定和必要的视觉交互 |
| C | Controller | `TomatoTimerPro/Controllers/` | 处理交互命令、协调业务操作、提供页面可绑定的展示状态；支持复用 |
| 工具层 | 通用工具 | `TomatoTimerPro/Tools/` | 提供无业务状态、无页面依赖的通用计算、转换和校验能力 |

四层必须分别放在四个顶级目录中。禁止将同一功能的仓库、Mgr、页面和控制器都塞进一个功能文件夹。

`Tools` 是 MMVC 之外的独立支撑层，与四层目录平级，不改变 MMVC 的职责划分。

## 2. 固定目录规划

以下路径相对于解决方案根目录。先按层分目录，再在层内按职责或功能拆分：

```text
TomatoTimerPro.sln
Rulers/
  WPF-MMVC.md
TomatoTimerPro/
  App.xaml                         # WPF 应用入口和应用级资源引用
  App.xaml.cs                      # 将系统生命周期事件转交 ApplicationMgr
  AssemblyInfo.cs
  TomatoTimerPro.csproj
  Bootstrap/
    ApplicationComposition.cs      # 统一创建并连接仓库、Mgr、Controller、View
  RuntimeStore/                    # 第一个 M：自行实现的内存仓库
    Abstractions/
      IRuntimeStore.cs
    Implementation/
      RuntimeStore.cs
    States/
      Timer/
        TimerState.cs
      Settings/
        SettingsState.cs
  Managers/                        # 第二个 M：软件与页面生命周期
    Application/
      ApplicationMgr.cs
    Pages/
      PageMgr.cs
      PageRegistration.cs
      PageScope.cs
  Views/                           # V：仅放视图及其视觉代码
    Shell/
      MainWindow.xaml
      MainWindow.xaml.cs
    Pages/
      Timer/
        TimerPage.xaml
        TimerPage.xaml.cs
      Settings/
        SettingsPage.xaml
        SettingsPage.xaml.cs
    Components/
      Countdown/
        CountdownView.xaml
        CountdownView.xaml.cs
  Controllers/                     # C：仅放控制器及展示状态
    Shell/
      MainWindowController.cs
    Pages/
      Timer/
        TimerPageController.cs
      Settings/
        SettingsPageController.cs
    Components/
      Countdown/
        CountdownController.cs
    Presentation/
      Timer/
        TimerPresentationState.cs
  Contracts/                       # 跨层的最小契约，不放具体实现
    Lifecycle/
      IPageLifecycle.cs
    Navigation/
      IPageNavigator.cs
      PageId.cs
  Infrastructure/                  # 技术设施，不承载页面业务
    Binding/
      ObservableObject.cs
    Commands/
      RelayCommand.cs
      AsyncRelayCommand.cs
    Threading/
      IUiDispatcher.cs
      WpfUiDispatcher.cs
    Persistence/                   # 需要落盘时再添加
  Tools/                           # 通用工具，不保存业务状态
    Text/
      TextFormatter.cs
    Time/
      DurationFormatter.cs
    Collections/
      CollectionExtensions.cs
    Validation/
      Guard.cs
  Resources/                       # 资源字典、样式、主题、图标
    Styles/
    Themes/
    Images/
```

### 文件归属规则

- `RuntimeStore`、`Managers`、`Views`、`Controllers` 不允许相互嵌套。
- `Tools` 与上述四层及 `Infrastructure` 平级；内部按通用能力分类，禁止混入页面、控制器、仓库或 Mgr。
- 新增功能时在相关层内创建同名子目录，例如 `Views/Pages/Statistics` 与 `Controllers/Pages/Statistics`；共享统计状态放 `RuntimeStore/States/Statistics`。
- 页面专用代码放 `Pages/<功能名>/`；能够独立复用的 UI 组件及控制器分别放 `Views/Components/`、`Controllers/Components/`。
- `Window`、`Page`、`UserControl` 及其 `.xaml.cs` 必须位于 `Views`，XAML 与后台文件必须相邻。
- Controller 不放在 View 的文件夹中；Mgr 不放在 Controller 的文件夹中。
- 共享业务状态放 `RuntimeStore/States`；仅用于展示的复杂数据类型放 `Controllers/Presentation/<功能名>`，简单展示属性可直接定义在 Controller 上。
- `Contracts` 只存放确实跨层使用的接口和标识；某层内部的接口留在该层，不为了凑结构全部上提。
- 禁止用根目录下的 `Common`、`Helpers`、`Utils`、`Models`、`Services` 作为不明确职责的堆放区。
- 命名空间与目录一致，例如 `TomatoTimerPro.Managers.Pages`、`TomatoTimerPro.Controllers.Components.Countdown`。
- 初期保持一个 WPF 项目，使用目录与代码审查维护边界；规模扩大后再按这些边界拆程序集。

### 现有文件迁移约定

当前项目的 `MainWindow.xaml` 和 `MainWindow.xaml.cs` 在项目根目录。实际进行架构改造时，将它们一起移到 `Views/Shell/`，同步更新命名空间、`x:Class` 及相关引用。

应用改为由 `ApplicationComposition` 和 `ApplicationMgr` 显式创建主窗口时，必须移除 `App.xaml` 的 `StartupUri`，避免重复创建窗口。该迁移属于后续实现工作，不能仅移动文件而遗漏启动配置。

## 3. 依赖与装配

```text
App → Bootstrap → 创建并连接各层
App → ApplicationMgr → PageMgr → 页面作用域、View 与 Controller
View → 绑定 Controller
Controller → RuntimeStore 抽象、导航契约和必要的服务契约
RuntimeStore 实现 → 仓库抽象与业务状态类型
基础设施实现 → 对应的技术契约
MMVC 各层、Infrastructure → 按需调用 Tools
Tools → .NET 基础类库
```

- `RuntimeStore` 不依赖 WPF、View、Controller 或 Mgr。
- Controller 不引用具体 View、`Window`、`Page`、`UserControl`、具体 Mgr，不直接操作可视树。
- Controller 需要导航时依赖 `IPageNavigator`，由 `PageMgr` 实现；禁止通过引用具体 `PageMgr` 形成循环依赖。
- `Contracts` 不依赖四层的具体实现，也不暴露 WPF 控件类型。
- `Tools` 不反向依赖 MMVC、`Contracts`、`Infrastructure` 或 `Bootstrap`，也不引用 WPF；业务层调用工具，工具不能回调业务层来编排业务。
- Mgr 通过注入的工厂创建页面作用域，工厂负责组装 View、Controller 和依赖；避免 Mgr 到处手动创建业务对象。
- 依赖从构造函数显式传入；禁止全局静态服务定位器、`App.Current.Services` 和随处调用的 `Instance` 单例。
- `ApplicationComposition` 是统一装配入口，可使用手写工厂；仅在必要时引入 DI 容器，不为使用框架而新增依赖。

## 4. 第一个 M：运行时缓存仓库

- 必须自行实现进程内缓存仓库；第三方缓存库或数据库不能代替该层的职责。
- 仓库保存运行期间需要共享、跨页面保留的业务状态，例如计时会话、应用设置和统计数据。
- 同一业务状态只有一个权威来源。Controller 不得再维护一份能独立修改的相同业务状态。
- 读取和更新使用强类型 API；禁止向业务调用方暴露 `Dictionary<string, object>` 或使用随意拼写的字符串键。
- 优先按业务状态类型注册唯一状态；同类型确有多个实例时，显式定义业务 ID 和作用域。
- 明确定义状态不存在时的行为，例如 `TryGet` 返回失败；禁止静默创建不完整对象。
- 提供必要的读取、原子更新、删除或清理能力；未使用的缓存功能不提前实现。
- 返回不可变状态或快照，禁止调用方绕过仓库直接修改内部集合和共享状态。
- 使用锁、串行队列或等价机制保证读改写原子性。`ConcurrentDictionary` 本身不能保证复合业务更新正确。
- 状态变更成功后才通知订阅者，并在释放内部锁后通知；明确通知线程和订阅解除方式。
- 仓库不保存 View、Controller、WPF 控件、Dispatcher、命令或事件订阅令牌。
- 仓库不负责页面导航、页面创建、定时业务调度或磁盘读写。计时等业务行为由相应 Controller 或明确的业务执行器协调。
- 运行时缓存默认不跨进程保存。持久化需要独立实现：启动时读取并写入仓库，退出前按策略保存。
- 计时精度基于时间源、开始时间和截止时间计算，不能只靠 UI 定时器每次减一维护真实剩余时间。

## 5. 第二个 M：Mgr

### ApplicationMgr：软件生命周期

- 管理一次性初始化、加载配置、打开主窗口、激活、失活、有序关闭和最终释放。
- 启动顺序：创建依赖与仓库 → 加载必要状态 → 初始化页面管理 → 显示主窗口。
- 激活与失活可以多次发生，不能重新执行一次性启动或自动销毁页面；是否暂停计时由明确产品规则决定。
- 退出顺序：阻止新操作 → 请求页面退出 → 取消并等待后台任务 → 保存必要数据 → 释放作用域、共享资源及仓库 → 关闭应用。
- 有异步保存或关闭确认时，在可取消的关闭入口先取消本次关闭，等待关闭流程完成后再允许关闭；不能在 `OnExit` 中启动未等待的异步保存。
- 关闭流程必须防重入。关闭被否决时，应恢复可操作状态；保存失败需按明确策略处理，不能静默丢失数据。
- 区分窗口关闭、应用退出和隐藏到托盘；仅在产品要求托盘模式时实现隐藏策略。
- `App.xaml.cs` 仅做 WPF 生命周期桥接和异常边界处理，不承载计时或页面业务。

### PageMgr：页面生命周期

- 统一管理页面注册、创建、进入、离开、保活、重新进入和销毁。
- 页面作用域持有该页的 View、Controller、取消令牌和释放资源；业务状态按约定存入仓库。
- 页面生命周期契约至少表达初始化、进入、离开请求和最终释放。初始化每实例一次，进入和离开可多次发生，释放后不可复用。
- 异步钩子使用 `Task` 或 `ValueTask` 并接收取消令牌，释放支持 `IDisposable` 或 `IAsyncDisposable`。
- 先完成离开检查，再提交当前页面切换；被否决时保留原页面。
- 页面切换串行化或取消旧请求；旧请求完成后不能覆盖更新的导航结果。
- 目标页创建或进入失败时，保留或恢复原页面，清理失败的目标作用域，并允许重试。
- 默认离开后销毁；需要保活时显式注册。保活只复用所属作用域，不重复初始化或重复订阅。
- 不能直接将 WPF `Loaded` / `Unloaded` 当作完整业务生命周期，它们可能因可视树变化多次触发。
- Mgr 只做生命周期编排，不变成容纳计时、设置、统计等全部业务逻辑的总管类。

## 6. V：页面与组件

- XAML 负责布局、绑定、样式和模板。页面通过 `DataContext` 绑定对应 Controller。
- `DataContext` 由装配工厂设置。View 不自行查找仓库或创建业务 Controller。
- 后台代码仅处理视觉行为，例如焦点、动画、拖拽窗口及控件事件到命令的适配。
- 禁止在 View 中处理业务状态、持久化、导航决策或后台业务任务。
- 可复用组件通过依赖属性或显式参数接收 Controller、命令及数据，不假设宿主页面的具体类型。
- 普通子组件不得擅自覆盖继承的 `DataContext`；需要独立 Controller 时使用显式绑定边界。
- Styles 和 Themes 中存放资源字典，由 `App.xaml` 合并；业务 Controller 不加载或切换具体控件样式。

## 7. C：可复用控制器

- Controller 接收用户意图，暴露命令与展示状态，协调仓库更新及必要的业务操作。
- Controller 可承担 WPF 绑定所需的 `INotifyPropertyChanged`，无需为了形式额外创建 ViewModel。
- 展示状态可包含选中项、错误提示、忙碌状态、格式化文本等；共享业务事实仍以仓库为准。
- 复用指同一个 Controller 类型可用于多个页面或组件，不代表默认共享同一个实例。
- 默认每个页面或组件实例拥有自己的 Controller；多个独立展示通过同一仓库同步业务状态。
- Controller 的构造参数只包含必要的仓库、服务契约和配置，不接收具体页面对象。
- 将可独立使用的能力拆成组件 Controller，例如 `CountdownController`；页面 Controller 通过组合使用它，不复制计时实现。
- 不写死控件名、页面资源键、宿主窗口类型或页面层级；页面差异通过参数或策略传入。
- 仅在多个宿主确实需要同一交互状态时共享实例，并明确所有者；只有所有者负责最终释放。
- Controller 不直接调用 `Application.Current`、`Dispatcher.CurrentDispatcher`、`MessageBox.Show` 或 `FindName`。需要 UI 调度或交互时注入对应抽象。
- 有生命周期资源的 Controller 参与页面作用域；关闭或销毁时取消任务、停止定时器、解除仓库和全局事件订阅。
- 临时离开与最终销毁必须区分：保活页面按需求暂停订阅或任务，重新进入时恢复，禁止重复订阅。
- 子 Controller 的所有权由构建它的作用域确定，避免父 Controller 和页面作用域重复释放同一实例。

## 8. Tools：工具层

- 固定放在 `TomatoTimerPro/Tools/`，命名空间与子目录对应，例如 `TomatoTimerPro.Tools.Time`。
- 存放可脱离当前业务复用的文本处理、时长格式化、集合扩展、参数校验和通用计算；按实际需求新增分类。
- 默认使用无状态函数；简单工具可定义为静态类，无需为了装配而创建接口或注册到容器。
- 输入、输出和必要配置必须显式传入；文化区域、格式规则、当前时间等不能从页面或全局服务中隐式获取。
- 工具不得保存运行时业务状态、全局可变缓存或页面引用；共享业务状态仍归 `RuntimeStore`。
- 工具不得管理页面或软件生命周期，不创建后台常驻任务、定时器，也不持有需要生命周期管理的资源。
- 与操作系统、文件、网络、持久化或 WPF 交互的能力放 `Infrastructure`；命令、属性通知和 UI 调度也留在 `Infrastructure`。
- 与当前业务有关的规则留在对应 Controller 或明确的业务类型中，例如番茄钟阶段切换、计时奖励、设置生效策略，不能为了复用都搬入工具层。
- 例如：`DurationFormatter` 只把传入的时长格式化为文本；剩余时间状态属于仓库，暂停操作属于 Controller，页面进入与销毁属于 Mgr。
- 类名体现具体能力，避免不断膨胀的 `Tools.cs`、`CommonHelper.cs` 或 `Utils.cs`；已有同职责工具优先复用。
- 明确空值、非法参数、边界值和异常行为，不静默吞掉异常或返回容易误判的默认值。

## 9. WPF 异步、线程与绑定

- UI 绑定对象和 `ObservableCollection` 的变更通过注入的 UI 调度器切回 UI 线程；仓库本身不依赖 UI 线程。
- 禁止在 UI 线程使用 `.Wait()`、`.Result` 或长时间阻塞操作。
- `async void` 仅用于 WPF 必须的事件适配入口，内部必须等待可观察的异步方法并处理异常。
- 异步命令必须处理异常、执行中状态、防重复执行和取消；结束后正确刷新 `CanExecute`。
- 页面异步工作绑定所属作用域的取消令牌；导航离开、销毁和应用退出分别执行约定的取消策略。
- 更新页面前检查取消状态与作用域有效性，避免旧任务将结果写回已离开的页面。
- 禁止未观察异常的后台任务；需要长期运行的任务必须有明确所有者、停止方式和异常处理。
- 仓库通知的线程与 UI 线程分开约定；订阅者负责通过调度器更新展示属性。

## 10. 新功能落地顺序

1. 判断共享业务状态与页面私有展示状态，确定各自唯一所有者。
2. 在 `RuntimeStore/States/<功能名>` 定义必要业务状态和仓库访问方式。
3. 在 `Controllers/Pages/<功能名>` 或 `Controllers/Components/<功能名>` 实现交互与命令。
4. 在 `Views` 对应目录实现 XAML，并绑定 Controller。
5. 在启动装配处注册工厂，由 PageMgr 负责页面作用域及生命周期。
6. 验证首次进入、重复进入、取消、失败恢复、销毁和应用退出。

不因为增加一个页面就创建一个新的 Mgr；只有出现独立的生命周期管理职责时才新增 Mgr。

需要通用计算或转换时，先检查 `Tools` 是否已有对应能力；新增工具必须符合第 8 节的职责边界。

## 11. 完成检查

- 四层分别位于规定目录，文件命名与命名空间一致。
- 通用工具独立位于 `Tools`，不依赖业务层、WPF 或基础设施，不持有业务状态或生命周期资源。
- View 没有业务逻辑，Controller 不依赖具体页面，仓库不引用 WPF。
- 共享业务状态没有多个可独立修改的副本；运行时缓存和持久化职责明确。
- 同一种组件 Controller 能在两个宿主中分别创建使用，无需修改其实现，也不串扰私有展示状态。
- 页面生命周期在重复导航、导航失败和保活场景下正确执行，事件订阅不重复。
- 页面销毁后无定时器、后台任务和事件订阅泄漏；应用退出等待必要保存与清理。
- 代码变更后运行相应构建及必要验证。重点测试仓库原子更新、生命周期顺序和 Controller 复用；文档修改无需运行 WPF 测试。
- 需求未要求时，不顺带重构其他模块、不修改无关用户改动、不提前引入大型框架。

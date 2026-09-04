namespace ServerDesk.App.Presentation;

public sealed record WorkspaceNavigationDefinition(
    string Route,
    string GroupKey,
    string TitleKey,
    string DescriptionKey,
    bool RequiresServer);

public static class WorkspaceNavigationCatalog
{
    public const string GlobalDashboard = "global-dashboard";
    public const string Dashboard = "dashboard";
    public const string Explorer = "explorer";
    public const string Terminal = "terminal";
    public const string Processes = "processes";
    public const string Services = "services";
    public const string Docker = "docker";
    public const string Storage = "storage";
    public const string Network = "network";
    public const string Logs = "logs";
    public const string Tunnels = "tunnels";
    public const string ScheduledTasks = "scheduled-tasks";
    public const string Git = "git";
    public const string Nginx = "nginx";
    public const string Tls = "tls";
    public const string EnvironmentFiles = "environment-files";
    public const string Deployment = "deployment";
    public const string Firewall = "firewall";
    public const string Users = "users";
    public const string Packages = "packages";
    public const string Databases = "databases";
    public const string DatabaseProfiles = "database-profiles";
    public const string Backups = "backups";
    public const string OperationHistory = "operation-history";
    public const string Organize = "organize";
    public const string ConnectionHistory = "connection-history";
    public const string ConnectionRoute = "connection-route";

    public static IReadOnlyList<WorkspaceNavigationDefinition> Items { get; } =
    [
        new(GlobalDashboard, "Loc.Shell.Group.Overview", "Loc.Shell.Nav.GlobalDashboard", "Loc.Shell.Nav.GlobalDashboard.Description", false),
        new(Dashboard, "Loc.Shell.Group.Overview", "Loc.Shell.Nav.Dashboard", "Loc.Shell.Nav.Dashboard.Description", true),

        new(Explorer, "Loc.Shell.Group.Work", "Loc.Shell.Nav.Explorer", "Loc.Shell.Nav.Explorer.Description", true),
        new(Terminal, "Loc.Shell.Group.Work", "Loc.Shell.Nav.Terminal", "Loc.Shell.Nav.Terminal.Description", true),

        new(Processes, "Loc.Shell.Group.Operate", "Loc.Shell.Nav.Processes", "Loc.Shell.Nav.Processes.Description", true),
        new(Services, "Loc.Shell.Group.Operate", "Loc.Shell.Nav.Services", "Loc.Shell.Nav.Services.Description", true),
        new(Docker, "Loc.Shell.Group.Operate", "Loc.Shell.Nav.Docker", "Loc.Shell.Nav.Docker.Description", true),
        new(Storage, "Loc.Shell.Group.Operate", "Loc.Shell.Nav.Storage", "Loc.Shell.Nav.Storage.Description", true),
        new(Network, "Loc.Shell.Group.Operate", "Loc.Shell.Nav.Network", "Loc.Shell.Nav.Network.Description", true),
        new(Logs, "Loc.Shell.Group.Operate", "Loc.Shell.Nav.Logs", "Loc.Shell.Nav.Logs.Description", true),
        new(Tunnels, "Loc.Shell.Group.Operate", "Loc.Shell.Nav.Tunnels", "Loc.Shell.Nav.Tunnels.Description", true),

        new(ScheduledTasks, "Loc.Shell.Group.Deploy", "Loc.Shell.Nav.ScheduledTasks", "Loc.Shell.Nav.ScheduledTasks.Description", true),
        new(Git, "Loc.Shell.Group.Deploy", "Loc.Shell.Nav.Git", "Loc.Shell.Nav.Git.Description", true),
        new(Nginx, "Loc.Shell.Group.Deploy", "Loc.Shell.Nav.Nginx", "Loc.Shell.Nav.Nginx.Description", true),
        new(Tls, "Loc.Shell.Group.Deploy", "Loc.Shell.Nav.Tls", "Loc.Shell.Nav.Tls.Description", true),
        new(EnvironmentFiles, "Loc.Shell.Group.Deploy", "Loc.Shell.Nav.EnvironmentFiles", "Loc.Shell.Nav.EnvironmentFiles.Description", true),
        new(Deployment, "Loc.Shell.Group.Deploy", "Loc.Shell.Nav.Deployment", "Loc.Shell.Nav.Deployment.Description", true),

        new(Firewall, "Loc.Shell.Group.Admin", "Loc.Shell.Nav.Firewall", "Loc.Shell.Nav.Firewall.Description", true),
        new(Users, "Loc.Shell.Group.Admin", "Loc.Shell.Nav.Users", "Loc.Shell.Nav.Users.Description", true),
        new(Packages, "Loc.Shell.Group.Admin", "Loc.Shell.Nav.Packages", "Loc.Shell.Nav.Packages.Description", true),
        new(Databases, "Loc.Shell.Group.Admin", "Loc.Shell.Nav.Databases", "Loc.Shell.Nav.Databases.Description", true),
        new(DatabaseProfiles, "Loc.Shell.Group.Admin", "Loc.Shell.Nav.DatabaseProfiles", "Loc.Shell.Nav.DatabaseProfiles.Description", true),
        new(Backups, "Loc.Shell.Group.Admin", "Loc.Shell.Nav.Backups", "Loc.Shell.Nav.Backups.Description", true),
        new(OperationHistory, "Loc.Shell.Group.Admin", "Loc.Shell.Nav.OperationHistory", "Loc.Shell.Nav.OperationHistory.Description", true),

        new(Organize, "Loc.Shell.Group.Server", "Loc.Shell.Nav.Organize", "Loc.Shell.Nav.Organize.Description", false),
        new(ConnectionHistory, "Loc.Shell.Group.Server", "Loc.Shell.Nav.ConnectionHistory", "Loc.Shell.Nav.ConnectionHistory.Description", false),
        new(ConnectionRoute, "Loc.Shell.Group.Server", "Loc.Shell.Nav.ConnectionRoute", "Loc.Shell.Nav.ConnectionRoute.Description", true),
    ];
}

public sealed record WorkspaceNavigationItem(
    string Route,
    string Group,
    string Title,
    string Description,
    bool IsAvailable,
    bool ShowGroupHeader);

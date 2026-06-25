namespace VestiCode.Core.Security;

/// <summary>全局权限档位。</summary>
public enum SecurityLevel
{
    /// <summary>严格：仅白名单路径放行，其余询问。</summary>
    Strict,

    /// <summary>默认：读放行、写询问。</summary>
    Normal,

    /// <summary>放行：仅黑名单拦截，其余全放行。</summary>
    Permissive,
}

/// <summary>规则命中后的动作。</summary>
public enum RuleAction
{
    Allow,
    Deny,
    Ask,
}

/// <summary>人在回路（HITL）的用户决策。</summary>
public enum HitlDecision
{
    /// <summary>本次允许。</summary>
    AllowOnce,

    /// <summary>本会话允许。</summary>
    AllowSession,

    /// <summary>永久允许（写入项目级规则文件）。</summary>
    AllowPermanent,

    /// <summary>拒绝。</summary>
    Deny,
}

/// <summary>HITL 决策 + 可选的用户拒绝原因（拒绝时回灌模型，促其调整）。</summary>
public sealed record HitlVerdict(HitlDecision Decision, string Reason = "");

/// <summary>规则作用域（优先级：会话 &gt; 项目 &gt; 全局）。</summary>
public enum RuleScope
{
    Session,
    Project,
    Global,
}

/// <summary>安全检查的最终判定（替代 mewcode 的 (bool, "ask") 二元组）。</summary>
public enum SecurityDecision
{
    /// <summary>放行执行。</summary>
    Allow,

    /// <summary>拒绝执行。</summary>
    Deny,

    /// <summary>需要人在回路确认。</summary>
    Ask,
}

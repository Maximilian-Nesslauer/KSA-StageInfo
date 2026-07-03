using CommunityToolkit.HighPerformance.Buffers;
using KSA;

namespace StageInfo.Analysis;

internal static class FlowHelpers
{
    /// <summary>
    /// The tank-node list a core actually draws from, mirroring the FlowRule
    /// switch in ResourceManager.Consume. PartTree.RecreateResourceManagers
    /// assigns thruster cores NearestToFurtherest (all-stage) and engine cores
    /// NearestToFurtherestSameStage by default; an engine core's rule is
    /// player-changeable via Part's Fuel Flow combo, a thruster's is not.
    /// Picking the rule-matched list keeps the analyzer's reachable-fuel set
    /// identical to what the game consumes, instead of hard-coding the
    /// same-stage subset.
    /// </summary>
    public static Tank[][]? SelectFlowNodes(ResourceManager rm) => rm.FlowRule switch
    {
        FlowRule.FurtherestToNearest => rm.FurtherestToNearestNode,
        FlowRule.NearestToFurtherest => rm.NearestToFurtherestNode,
        FlowRule.FurtherestToNearestSameStage => rm.FurtherestToNearestNodeSameStage,
        FlowRule.NearestToFurtherestSameStage => rm.NearestToFurtherestNodeSameStage,
        _ => rm.NearestToFurtherestNode,
    };
}

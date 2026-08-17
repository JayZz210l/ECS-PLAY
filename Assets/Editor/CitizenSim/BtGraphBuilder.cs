using System.IO;
using System.Linq;
using Unity.Behavior;
using Unity.Behavior.GraphFramework;
using UnityEditor;
using UnityEngine;
using NodeRegistry = Unity.Behavior.NodeRegistry;

namespace CitizenSim.EditorTools
{
    // 创建标准 BehaviorAuthoringGraph(源图)资产,可在 Behavior 编辑器双击打开、可视化编辑。
    // 程序化连好整棵树,用户打开即可见、可改。保存时 GraphAssetProcessor 自动烘焙运行时子资产。
    //
    // 树(M4 完整:威胁 > 饥饿 > 疲劳 > 无聊 > 漫游):
    //   Start (Repeat)
    //     -> Conditional Branch [IsThreatened]
    //          True  -> Flee
    //          False -> Conditional Branch [IsHungry]
    //                     True  -> SeekFood
    //                     False -> Conditional Branch [IsFatigued]
    //                                True  -> SeekHome
    //                                False -> Conditional Branch [IsBored]
    //                                           True  -> SeekFun
    //                                           False -> Wander
    //
    // 用 internal Unity.Behavior.* API(GraphAsset.CreateNode / PortModel.ConnectTo / NodeRegistry /
    // ConditionModel),靠 Assembly-CSharp-Editor 的 IVT 访问。
    //
    // 两个菜单:
    //   - Build ...:非破坏式。资产已存在则跳过(保护手动编辑),不存在才生成。
    //   - Force Rebuild ...:删除已存在资产并重新生成。仅在确需从代码重建时用。
    public static class BtGraphBuilder
    {
        const string AssetPath = "Assets/Resources/CitizenBehavior.asset";

        [MenuItem("Tools/CitizenSim/Build CitizenBehavior Graph Asset")]
        public static void Build()
        {
            if (AssetDatabase.LoadMainAssetAtPath(AssetPath) != null)
            {
                var existing = AssetDatabase.LoadMainAssetAtPath(AssetPath);
                Selection.activeObject = existing;
                Debug.Log($"CitizenBehavior 已存在,跳过生成(保护手动编辑)。如需重建用 Tools/CitizenSim/Force Rebuild CitizenBehavior Graph Asset。", existing);
                return;
            }
            BuildInternal();
        }

        [MenuItem("Tools/CitizenSim/Force Rebuild CitizenBehavior Graph Asset")]
        public static void ForceRebuild()
        {
            if (AssetDatabase.LoadMainAssetAtPath(AssetPath) != null)
            {
                bool ok = EditorUtility.DisplayDialog(
                    "Force Rebuild CitizenBehavior",
                    $"将删除并重建 {AssetPath}。\n已存在的资产(含手动编辑)会被覆盖,确定?",
                    "重建", "取消");
                if (!ok) return;
            }
            BuildInternal();
        }

        static void BuildInternal()
        {
            EnsureFolder(Path.GetDirectoryName(AssetPath).Replace('\\', '/'));
            AssetDatabase.DeleteAsset(AssetPath);

            var graph = ScriptableObject.CreateInstance<BehaviorAuthoringGraph>();
            graph.name = "CitizenBehavior";
            AssetDatabase.CreateAsset(graph, AssetPath);
            // CreateAsset 触发导入校验,EnsureAtLeastOneRoot 会自动加一个 Start 根节点。

            // 取自动加的 Start(没有就建一个),设 Repeat。
            StartNodeModel start;
            if (graph.Roots.Count > 0 && graph.Roots[0] is StartNodeModel s)
                start = s;
            else
                start = (StartNodeModel)graph.CreateNode(
                    typeof(StartNodeModel), new Vector2(0, 0), null,
                    new object[] { NodeRegistry.GetInfo(typeof(Start)) });
            start.Repeat = true;
            PortModel startOut = start.OutputPortModels.First();

            // 嵌套 Conditional Branch:Hungry > Fatigued > Bored > Wander(优先级从高到低)
            // 威胁分支(最高优先级,M4):Threatened -> Flee;否则进入日常树。
            var bThreat = CreateBranch(graph, startOut, new Vector2(0, 0), typeof(IsThreatenedCondition));
            CreateAction(graph, bThreat.FindPortModelByName("True"), new Vector2(-660, 160), typeof(FleeAction));

            var bHungry = CreateBranch(graph, bThreat.FindPortModelByName("False"), new Vector2(0, 160), typeof(IsHungryCondition));
            CreateAction(graph, bHungry.FindPortModelByName("True"), new Vector2(-330, 340), typeof(SeekFoodAction));

            var bFatigued = CreateBranch(graph, bHungry.FindPortModelByName("False"), new Vector2(330, 340), typeof(IsFatiguedCondition));
            CreateAction(graph, bFatigued.FindPortModelByName("True"), new Vector2(330, 520), typeof(SeekHomeAction));

            var bBored = CreateBranch(graph, bFatigued.FindPortModelByName("False"), new Vector2(660, 520), typeof(IsBoredCondition));
            CreateAction(graph, bBored.FindPortModelByName("True"), new Vector2(660, 700), typeof(SeekFunAction));
            CreateAction(graph, bBored.FindPortModelByName("False"), new Vector2(990, 700), typeof(WanderAction));

            // ValidateAsset 补主黑板、条件 Asset 引用等;BuildRuntimeGraph 烘焙运行时子资产。
            graph.ValidateAsset();
            graph.BuildRuntimeGraph(true);

            EditorUtility.SetDirty(graph);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(AssetPath, ImportAssetOptions.ForceUpdate);

            Selection.activeObject = graph;
            Debug.Log($"Built CitizenBehavior authoring graph: Nodes={graph.Nodes.Count}, " +
                      $"Roots={graph.Roots.Count}, HasRuntimeGraph={graph.HasRuntimeGraph}.", graph);
        }

        static BranchingConditionNodeModel CreateBranch(BehaviorAuthoringGraph graph, PortModel inputPort, Vector2 pos, System.Type condType)
        {
            var branch = (BranchingConditionNodeModel)graph.CreateNode(
                typeof(BranchingConditionNodeModel), pos, inputPort,
                new object[] { NodeRegistry.GetInfo(typeof(BranchingConditionComposite)) });
            var info = ConditionUtility.GetInfoForConditionType(condType);
            var cond = (Condition)System.Activator.CreateInstance(condType);
            ((IConditionalNodeModel)branch).ConditionModels.Add(new ConditionModel(branch, cond, info));
            return branch;
        }

        static void CreateAction(BehaviorAuthoringGraph graph, PortModel port, Vector2 pos, System.Type actionType)
        {
            graph.CreateNode(
                typeof(ActionNodeModel), pos, port,
                new object[] { NodeRegistry.GetInfo(actionType) });
        }

        static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }
    }
}

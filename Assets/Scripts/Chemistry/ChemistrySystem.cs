using UnityEngine;

namespace Chemistry
{
    /// <summary>
    /// 化学系统全局管理器（单例）
    /// 提供游戏中对化学试剂数据库的统一访问入口
    /// </summary>
    public class ChemistrySystem : MonoBehaviour
    {
        [Tooltip("化学试剂数据库资源（拖拽赋值）")]
        public ChemicalReagentDatabase database;

        private static ChemistrySystem s_instance;
        public static ChemistrySystem Instance
        {
            get
            {
                if (s_instance == null)
                {
                    s_instance = FindObjectOfType<ChemistrySystem>();
                    if (s_instance == null)
                        Debug.LogWarning("[ChemistrySystem] 场景中未找到 ChemistrySystem，请确保已挂载该组件。");
                }
                return s_instance;
            }
        }

        public static ChemicalReagentDatabase Database => Instance != null ? Instance.database : null;

        private void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }
            s_instance = this;
        }

        /// <summary>
        /// 按名称查找试剂（便捷静态方法）
        /// </summary>
        public static ChemicalReagent FindReagent(string name)
        {
            return Database != null ? Database.FindByName(name) : null;
        }

        /// <summary>
        /// 按「名称 + 物态」查找试剂（便捷静态方法）。
        /// 用于区分同种物质的不同物态，例如「固体硫酸铜」与「液体硫酸铜」。
        /// </summary>
        public static ChemicalReagent FindReagent(string name, PhysicalState state)
        {
            return Database != null ? Database.FindByNameAndState(name, state) : null;
        }

        /// <summary>
        /// 按化学式查找试剂（便捷静态方法）
        /// </summary>
        public static ChemicalReagent FindReagentByFormula(string formula)
        {
            return Database != null ? Database.FindByFormula(formula) : null;
        }

        /// <summary>
        /// 检查数据库是否已加载
        /// </summary>
        public static bool IsReady => Database != null;
    }
}

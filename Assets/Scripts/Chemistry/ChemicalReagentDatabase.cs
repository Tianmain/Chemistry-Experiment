using System.Collections.Generic;
using UnityEngine;

namespace Chemistry
{
    /// <summary>
    /// 化学试剂数据库（ScriptableObject）
    /// 集中管理所有 ChemicalReagent 资源，提供多种查询方式
    /// 可在 Project 窗口通过右键 Create > Chemistry > Reagent Database 创建实例
    /// </summary>
    [CreateAssetMenu(fileName = "ReagentDatabase", menuName = "Chemistry/Reagent Database")]
    public class ChemicalReagentDatabase : ScriptableObject
    {
        [Tooltip("所有已注册的化学试剂")]
        public List<ChemicalReagent> reagents = new List<ChemicalReagent>();

        /// <summary>
        /// 根据中文/英文/化学式/别名查找试剂（精确匹配）
        /// </summary>
        public ChemicalReagent FindByName(string query)
        {
            if (string.IsNullOrEmpty(query) || reagents == null) return null;
            query = query.Trim();
            foreach (var r in reagents)
            {
                if (r == null) continue;
                if (r.MatchesName(query))
                    return r;
            }
            return null;
        }

        /// <summary>
        /// 按「名称 + 物态」精确查找试剂。用于区分同种物质的不同物态，
        /// 例如同时存在「固体硫酸铜」与「液体（溶液）硫酸铜」时，
        /// 通过 state 参数锁定到具体某一态，避免两个同名资产互相干扰。
        /// </summary>
        public ChemicalReagent FindByNameAndState(string query, PhysicalState state)
        {
            if (string.IsNullOrEmpty(query) || reagents == null) return null;
            query = query.Trim();
            foreach (var r in reagents)
            {
                if (r == null) continue;
                if (r.defaultState == state && r.MatchesName(query))
                    return r;
            }
            return null;
        }

        /// <summary>
        /// 根据化学式查找试剂（精确匹配）
        /// </summary>
        public ChemicalReagent FindByFormula(string formula)
        {
            if (string.IsNullOrEmpty(formula) || reagents == null) return null;
            formula = formula.Trim();
            foreach (var r in reagents)
            {
                if (r == null) continue;
                if (r.chemicalFormula == formula)
                    return r;
            }
            return null;
        }

        /// <summary>
        /// 根据 CAS 号查找试剂
        /// </summary>
        public ChemicalReagent FindByCAS(string cas)
        {
            if (string.IsNullOrEmpty(cas) || reagents == null) return null;
            cas = cas.Trim();
            foreach (var r in reagents)
            {
                if (r == null) continue;
                if (r.casNumber == cas)
                    return r;
            }
            return null;
        }

        /// <summary>
        /// 按酸碱性筛选试剂
        /// </summary>
        public List<ChemicalReagent> FindByAcidity(AcidityType type)
        {
            var result = new List<ChemicalReagent>();
            if (reagents == null) return result;
            foreach (var r in reagents)
            {
                if (r != null && r.acidityType == type)
                    result.Add(r);
            }
            return result;
        }

        /// <summary>
        /// 按物态筛选试剂
        /// </summary>
        public List<ChemicalReagent> FindByState(PhysicalState state)
        {
            var result = new List<ChemicalReagent>();
            if (reagents == null) return result;
            foreach (var r in reagents)
            {
                if (r != null && r.defaultState == state)
                    result.Add(r);
            }
            return result;
        }

        /// <summary>
        /// 查找所有易燃试剂
        /// </summary>
        public List<ChemicalReagent> FindFlammableReagents()
        {
            var result = new List<ChemicalReagent>();
            if (reagents == null) return result;
            foreach (var r in reagents)
            {
                if (r != null && r.isFlammable)
                    result.Add(r);
            }
            return result;
        }

        /// <summary>
        /// 查找所有有毒试剂
        /// </summary>
        public List<ChemicalReagent> FindToxicReagents()
        {
            var result = new List<ChemicalReagent>();
            if (reagents == null) return result;
            foreach (var r in reagents)
            {
                if (r != null && r.isToxic)
                    result.Add(r);
            }
            return result;
        }

        /// <summary>
        /// 注册新试剂（避免重复注册）
        /// </summary>
        public void Register(ChemicalReagent reagent)
        {
            if (reagent == null || reagents == null) return;
            if (!reagents.Contains(reagent))
                reagents.Add(reagent);
        }

        /// <summary>
        /// 移除试剂
        /// </summary>
        public void Unregister(ChemicalReagent reagent)
        {
            if (reagent == null || reagents == null) return;
            reagents.Remove(reagent);
        }

        /// <summary>
        /// 获取数据库中试剂总数
        /// </summary>
        public int Count => reagents != null ? reagents.Count : 0;
    }
}

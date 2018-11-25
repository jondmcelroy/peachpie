﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Peachpie.CodeAnalysis.Utilities;

namespace Pchp.CodeAnalysis.Semantics.Graph
{
    /// <summary>
    /// Enables to transform a <see cref="ControlFlowGraph"/> in a straightforward manner.
    /// </summary>
    /// <remarks>
    /// The transformation is performed in two phases: updating and repairing. During updating,
    /// virtual Visit* and OnVisit* methods are called on all the nodes, edges and operations
    /// in the CFG. <see cref="ExploredColor"/> is used in the update to mark all the visited
    /// nodes and prevent infinite recursion. Any update of the graph marks all the changed nodes
    /// (their new versions) as <see cref="ChangedColor"/>. If such an update happens, in the
    /// repairing phase the whole graph is traversed again and all the unmodified blocks are cloned,
    /// the edges fixed and all the blocks in the final graph marked as <see cref="RepairedColor"/>.
    /// 
    /// This is done to prevent the graph nodes from pointing to nodes of the older version of the
    /// graph. Possible optimization would be to allow sharing graph parts in acyclic CFGs.
    /// </remarks>
    public class GraphRewriter : GraphUpdater
    {
        private Dictionary<BoundBlock, BoundBlock> _updatedBlocks;
        private List<BoundBlock> _possiblyUnreachableBlocks;

        public int ExploredColor { get; private set; }

        public int ChangedColor { get; private set; }

        public int RepairedColor { get; private set; }

        #region Helper classes

        /// <summary>
        /// Clones unmodified blocks and fixes the edges between them so that no edge targets
        /// a block from the previous version of the graph.
        /// </summary>
        private class GraphRepairer : GraphUpdater
        {
            private readonly GraphRewriter _rewriter;

            public GraphRepairer(GraphRewriter rewriter)
            {
                _rewriter = rewriter;
            }

            private BoundBlock Repair(BoundBlock block)
            {
                if (_rewriter.IsRepaired(block))
                {
                    return block;
                }

                if (!_rewriter.IsChanged(block))
                {
                    if (!_rewriter._updatedBlocks.TryGetValue(block, out var repaired))
                    {
                        repaired = block.Clone();
                        _rewriter.MapToNewVersion(block, repaired);
                    }

                    block = repaired;
                }

                if (!_rewriter.IsRepaired(block))
                {
                    block.Tag = _rewriter.RepairedColor;
                    block.NextEdge = (Edge)Accept(block.NextEdge);
                }

                return block;
            }

            public sealed override object VisitCFGBlock(BoundBlock x) => Repair(x);

            public sealed override object VisitCFGStartBlock(StartBlock x) => Repair(x);

            public sealed override object VisitCFGExitBlock(ExitBlock x) => Repair(x);

            public sealed override object VisitCFGCatchBlock(CatchBlock x) => Repair(x);

            public sealed override object VisitCFGCaseBlock(CaseBlock x) => Repair(x);
        }

        /// <summary>
        /// Finds all yield statements in unreachable blocks (those not yet colored by <see cref="ExploredColor"/>)
        /// of the original graph. The blocks encountered along the way are coloured as a side effect.
        /// </summary>
        private class UnreachableYieldFinder : GraphExplorer<VoidStruct>
        {
            public List<BoundYieldStatement> Yields { get; private set; }

            public UnreachableYieldFinder(int exploredColor) : base(exploredColor)
            { }

            public override VoidStruct VisitYieldStatement(BoundYieldStatement boundYieldStatement)
            {
                if (Yields == null)
                {
                    Yields = new List<BoundYieldStatement>();
                }

                Yields.Add(boundYieldStatement);

                return base.VisitYieldStatement(boundYieldStatement);
            }
        }

        #endregion

        #region Helper methods

        private bool IsExplored(BoundBlock x) => x.Tag >= ExploredColor;

        private bool IsChanged(BoundBlock x) => x.Tag >= ChangedColor;

        private bool IsRepaired(BoundBlock x) => x.Tag == RepairedColor;

        private BoundBlock TryGetNewVersion(BoundBlock block)
        {
            if (_updatedBlocks == null || !_updatedBlocks.TryGetValue(block, out var mappedBlock))
            {
                return block;
            }
            else
            {
                return mappedBlock;
            }
        }

        private void MapToNewVersion(BoundBlock oldBlock, BoundBlock newBlock)
        {
            newBlock.Tag = ChangedColor;

            if (_updatedBlocks == null)
            {
                _updatedBlocks = new Dictionary<BoundBlock, BoundBlock>();
            }

            _updatedBlocks.Add(oldBlock, newBlock);
        }

        private BoundBlock MapIfUpdated(BoundBlock original, BoundBlock updated)
        {
            if (original == updated)
            {
                return original;
            }
            else
            {
                MapToNewVersion(original, updated);
                return updated;
            }
        }

        /// <summary>
        /// Inform about a possible unreachability of this block due to a change in the graph.
        /// </summary>
        protected void NotePossiblyUnreachable(BoundBlock block)
        {
            if (_possiblyUnreachableBlocks == null)
            {
                _possiblyUnreachableBlocks = new List<BoundBlock>();
            }

            _possiblyUnreachableBlocks.Add(block);
        }

        #endregion

        #region ControlFlowGraph

        public sealed override object VisitCFG(ControlFlowGraph x)
        {
            OnVisitCFG(x);

            ExploredColor = x.NewColor();
            ChangedColor = x.NewColor();
            RepairedColor = x.NewColor();
            _updatedBlocks = null;

            // Traverse the whole graph and possibly obtain new versions of start and exit
            var updatedStart = (StartBlock)Accept(x.Start);
            var updatedExit = TryGetNewVersion(x.Exit);

            // Rescan and repair nodes and edges if any blocks were modified
            if (_updatedBlocks != null)
            {
                var repairer = new GraphRepairer(this);
                updatedStart = (StartBlock)updatedStart.Accept(repairer);
                updatedExit = _updatedBlocks[x.Exit];                       // It must have been updated by repairer
            }

            // Handle yields and unreachable blocks
            var yields = x.Yields;
            var unreachableBlocks = x.UnreachableBlocks;
            if (_possiblyUnreachableBlocks != null)
            {
                unreachableBlocks = unreachableBlocks.AddRange(_possiblyUnreachableBlocks.Where(b => !IsExplored(b)));

                // Remove any yields found in the unreachable blocks, eventually marking all the original blocks as explored
                if (!yields.IsDefaultOrEmpty && unreachableBlocks != x.UnreachableBlocks)
                {
                    var yieldFinder = new UnreachableYieldFinder(ExploredColor);

                    for (int i = x.UnreachableBlocks.Length; i < unreachableBlocks.Length; i++)
                    {
                        yieldFinder.VisitCFGBlock(unreachableBlocks[i]);
                    }

                    if (yieldFinder.Yields != null)
                    {
                        yields = yields.RemoveRange(yieldFinder.Yields);
                    }
                }
            }

            // Create a new CFG from the new versions of blocks and edges (expressions and statements are reused where unchanged)
            return x.Update(
                updatedStart,
                updatedExit,
                x.Labels,           // Keep all the labels, they are here only for the diagnostic purposes
                yields,
                unreachableBlocks);
        }

        protected virtual void OnVisitCFG(ControlFlowGraph x)
        { }

        #endregion

        #region Graph.Block

        protected sealed override object DefaultVisitBlock(BoundBlock x) => throw new InvalidOperationException();

        public sealed override object VisitCFGBlock(BoundBlock x)
        {
            if (IsExplored(x))
            {
                return x;
            }
            else
            {
                x.Tag = ExploredColor;
                return MapIfUpdated(x, OnVisitCFGBlock(x));
            }
        }

        public sealed override object VisitCFGStartBlock(StartBlock x)
        {
            if (IsExplored(x))
            {
                return x;
            }
            else
            {
                x.Tag = ExploredColor;
                return MapIfUpdated(x, OnVisitCFGStartBlock(x));
            }
        }

        public sealed override object VisitCFGExitBlock(ExitBlock x)
        {
            if (IsExplored(x))
            {
                return x;
            }
            else
            {
                x.Tag = ExploredColor;
                return MapIfUpdated(x, OnVisitCFGExitBlock(x));
            }
        }

        public sealed override object VisitCFGCatchBlock(CatchBlock x)
        {
            if (IsExplored(x))
            {
                return x;
            }
            else
            {
                x.Tag = ExploredColor;
                return MapIfUpdated(x, OnVisitCFGCatchBlock(x));
            }
        }

        public sealed override object VisitCFGCaseBlock(CaseBlock x)
        {
            if (IsExplored(x))
            {
                return x;
            }
            else
            {
                x.Tag = ExploredColor;
                return MapIfUpdated(x, OnVisitCFGCaseBlock(x));
            }
        }

        public virtual BoundBlock OnVisitCFGBlock(BoundBlock x) => (BoundBlock)base.VisitCFGBlock(x);

        public virtual StartBlock OnVisitCFGStartBlock(StartBlock x) => (StartBlock)base.VisitCFGStartBlock(x);

        public virtual ExitBlock OnVisitCFGExitBlock(ExitBlock x) => (ExitBlock)base.VisitCFGExitBlock(x);

        public virtual CatchBlock OnVisitCFGCatchBlock(CatchBlock x) => (CatchBlock)base.VisitCFGCatchBlock(x);

        public virtual CaseBlock OnVisitCFGCaseBlock(CaseBlock x) => (CaseBlock)base.VisitCFGCaseBlock(x);

        #endregion
    }
}

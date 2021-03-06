﻿using System;
using SourcePawn;

namespace Lysis
{
    public class ForwardTypePropagation : NodeVisitor
    {
        private readonly NodeGraph graph_;
        private NodeBlock block_;

        public ForwardTypePropagation(NodeGraph graph)
        {
            graph_ = graph;
        }

        public void propagate()
        {
            for (var i = 0; i < graph_.numBlocks; i++)
            {
                block_ = graph_[i];
                for (var iter = block_.nodes.begin(); iter.more(); iter.next())
                {
                    iter.node.accept(this);
                }
            }
        }
        public override void visit(DConstant node)
        {
        }
        public override void visit(DDeclareLocal local)
        {
            var var = graph_.file.lookupVariable(local.pc, local.offset);
            local.setVariable(var);

            if (var != null)
            {
                var tu = TypeUnit.FromVariable(var);
                //Debug.Assert(tu != null);
                local.addType(new TypeUnit(tu));
            }
        }
        public override void visit(DLocalRef lref)
        {
            var local = lref.local;
            if (local == null)
            {
                return;
            }
            var localTypes = local.typeSet;
            lref.addTypes(localTypes);
        }
        public override void visit(DJump jump)
        {
        }
        public override void visit(DJumpCondition jcc)
        {
            jcc.typeSet.addType(new TypeUnit(new PawnType(CellType.Bool)));
        }
        public override void visit(DSysReq sysreq)
        {
        }
        public override void visit(DBinary binary)
        {
        }
        public override void visit(DBoundsCheck check)
        {
            check.getOperand(0).setUsedAsArrayIndex();
        }
        public override void visit(DArrayRef aref)
        {
            var abase = aref.abase;
            var baseTypes = abase.typeSet;
            for (var i = 0; i < baseTypes.numTypes; i++)
            {
                aref.addType(baseTypes[i]);
            }
        }
        public override void visit(DStore store)
        {
        }
        public override void visit(DLoad load)
        {
            var fromTypes = load.from.typeSet;
            for (var i = 0; i < fromTypes.numTypes; i++)
            {
                var tu = fromTypes[i];
                var actual = tu.load();
                if (actual == null)
                {
                    actual = tu;
                }

                load.addType(actual);
            }
        }
        public override void visit(DReturn ret)
        {
        }
        public override void visit(DGlobal global)
        {
            if (global.var == null)
            {
                return;
            }

            var tu = TypeUnit.FromVariable(global.var);
            global.addType(tu);
        }
        public override void visit(DString node)
        {
        }
        public override void visit(DCall call)
        {
        }
    }

    public class BackwardTypePropagation : NodeVisitor
    {
        private readonly NodeGraph graph_;
        private NodeBlock block_;

        public BackwardTypePropagation(NodeGraph graph)
        {
            graph_ = graph;
        }

        public void propagate()
        {
            for (var i = graph_.numBlocks - 1; i >= 0; i--)
            {
                block_ = graph_[i];
                for (var iter = block_.nodes.rbegin(); iter.more(); iter.next())
                {
                    iter.node.accept(this);
                }
            }
        }

        private void propagateInputs(DNode lhs, DNode rhs)
        {
            lhs.typeSet.addTypes(rhs.typeSet);
            rhs.typeSet.addTypes(lhs.typeSet);
        }

        private DNode ConstantToReference(DConstant node, TypeUnit tu)
        {
            var global = graph_.file.lookupGlobal(node.value);
            if (global == null)
            {
                graph_.file.lookupVariable(node.pc, node.value, Scope.Static);
            }

            if (global != null)
            {
                return new DGlobal(global);
            }

            if (tu != null && tu.type.isString)
            {
                return new DString(graph_.file.stringFromData(node.value));
            }

            return null;
        }

        public override void visit(DConstant node)
        {
            DNode replacement = null;
            if (node.typeSet.numTypes == 1)
            {
                var tu = node.typeSet[0];
                switch (tu.kind)
                {
                    case TypeUnit.Kind.Cell:
                        {
                            switch (tu.type.type)
                            {
                                case CellType.Bool:
                                    replacement = new DBoolean(node.value != 0);
                                    break;
                                case CellType.Character:
                                    replacement = new DCharacter(Convert.ToChar(node.value));
                                    break;
                                case CellType.Float:
                                    {
                                        //Debug.Assert(BitConverter.IsLittleEndian);
                                        var bits = BitConverter.GetBytes(node.value);
                                        var v = BitConverter.ToSingle(bits, 0);
                                        replacement = new DFloat(v);
                                        break;
                                    }
                                case CellType.Function:
                                    {
                                        var p = graph_.file.publics[node.value >> 1];
                                        var function = graph_.file.lookupFunction(p.address);
                                        replacement = new DFunction(p.address, function);
                                        break;
                                    }
                                default:
                                    return;
                            }
                            break;
                        }

                    case TypeUnit.Kind.Array:
                        {
                            replacement = ConstantToReference(node, tu);
                            break;
                        }

                    default:
                        return;
                }
            }

            if (replacement == null && node.usedAsReference)
            {
                replacement = ConstantToReference(node, null);
            }

            if (replacement != null)
            {
                block_.nodes.insertAfter(node, replacement);
                node.replaceAllUsesWith(replacement);
            }
        }
        public override void visit(DDeclareLocal local)
        {
            if (local.value != null && local.var == null)
            {
                local.value.addTypes(local.typeSet);
            }
        }
        public override void visit(DLocalRef lref)
        {
            var local = lref.local;
            if (local != null)
            {
                lref.addTypes(local.typeSet);
            }
        }
        public override void visit(DJump jump)
        {
        }
        public override void visit(DJumpCondition jcc)
        {
            if (jcc.getOperand(0).type == NodeType.Binary)
            {
                var binary = (DBinary)jcc.getOperand(0);
                propagateInputs(binary.lhs, binary.rhs);
            }
        }

        private void visitSignature(DNode call, Signature signature)
        {
            for (var i = 0; i < call.numOperands && i < signature.args.Length; i++)
            {
                var node = call.getOperand(i);
                var arg = i < signature.args.Length
                               ? signature.args[i]
                               : signature.args[signature.args.Length - 1];

                var tu = TypeUnit.FromArgument(arg);
                if (tu != null)
                {
                    node.addType(tu);
                }
            }

            // Peek ahead for constants.
            if (signature.args.Length > 0 &&
                signature.args[signature.args.Length - 1].type == VariableType.Variadic)
            {
                for (var i = signature.args.Length - 1; i < call.numOperands; i++)
                {
                    var node = call.getOperand(i);
                    if (node.type != NodeType.Constant)
                    {
                        continue;
                    }

                    var constNode = (DConstant)node;
                    var global = graph_.file.lookupGlobal(constNode.value);
                    if (global != null)
                    {
                        call.replaceOperand(i, new DGlobal(global));
                        continue;
                    }

                    // Guess a string...
                    call.replaceOperand(i, new DString(graph_.file.stringFromData(constNode.value)));
                }
            }
        }

        public override void visit(DCall call)
        {
            visitSignature(call, call.function);
        }
        public override void visit(DSysReq sysreq)
        {
            visitSignature(sysreq, sysreq.native);
        }
        public override void visit(DBinary binary)
        {
            if (binary.spop == SPOpcode.add && binary.usedAsReference)
            {
                binary.lhs.setUsedAsReference();
            }
        }
        public override void visit(DBoundsCheck check)
        {
        }
        public override void visit(DArrayRef aref)
        {
            aref.abase.setUsedAsReference();
        }
        public override void visit(DStore store)
        {
            store.getOperand(0).setUsedAsReference();
        }
        public override void visit(DLoad load)
        {
            load.from.setUsedAsReference();
            if (load.from.typeSet != null && load.from.typeSet.numTypes == 1)
            {
                var tu = load.from.typeSet[0];
                if (tu.kind == TypeUnit.Kind.Array)
                {
                    var cv = new DConstant(0);
                    var aref = new DArrayRef(load.from, cv, 1);
                    block_.nodes.insertAfter(load.from, cv);
                    block_.nodes.insertAfter(cv, aref);
                    load.replaceOperand(0, aref);
                }
            }
        }
        public override void visit(DReturn ret)
        {
            if (graph_.function != null)
            {
                var input = ret.getOperand(0);
                var tu = TypeUnit.FromTag(graph_.function.returnType);
                input.typeSet.addType(tu);
            }
        }
        public override void visit(DGlobal global)
        {
        }
        public override void visit(DString node)
        {
        }
    }
}

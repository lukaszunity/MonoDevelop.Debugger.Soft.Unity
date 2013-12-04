using Mono.Debugger.Soft;
using Mono.Debugging.Evaluation;
using Mono.Debugging.Soft;

namespace MonoDevelop.Debugger.Soft.Unity
{
	internal class UnitySoftDebuggerAdaptor : SoftDebuggerAdaptor
	{
		public override bool IsNull (EvaluationContext ctx, object val)
		{
			//if  the "normal" way thinks we are null, then we definitely are
			if (!base.IsNull (ctx, val))
				return false;

			//otherwise, we might be one of unity's weirdo objects: a managed wrapper that derives from UnityEngine.Object.
			//Unity does this weird thing where the native side that's being wrapped can be dead, and then we set the instanceid of the
			//managed wrapper to 0.  we then overload the isbool operator, to return true for these managed objects whose native side is
			//now dead.  in order for the debugger to also show them as dead, we are checking here if we are dealing with a UnityEngine.Object
			//wrapper, and if so, we query its instanceid. if it's zero, then we treat this object as "Null".
			var objekt = val as ObjectMirror;
			if (objekt == null)
				return true;

			var cx = (SoftEvaluationContext)ctx;
			if (!DerivesFromUnityEngineObject (cx, objekt.Type))
				return true;
			
			MethodMirror method = OverloadResolve(cx, objekt.Type, "GetInstanceID", new TypeMirror[] { }, new TypeMirror[]{}, true, false, false);
			var returnValue = cx.RuntimeInvoke (method, val, new Value[] {}) as PrimitiveValue;
			return (returnValue != null && (int)returnValue.Value == 0);
		}

		bool DerivesFromUnityEngineObject(SoftEvaluationContext ctx, TypeMirror type)
		{
			if (type.FullName == "UnityEngine.Object")
				return true;
			
			var baseType = (TypeMirror)GetBaseType (ctx, type, false);
			if (baseType == null)
				return false;

			return DerivesFromUnityEngineObject (ctx, baseType);
		}
	}
}
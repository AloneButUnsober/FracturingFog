// Models/UserBulbChainStep.cs
//
// One named step in a chain. Source is the body of:
//   Vec3 Step(Vec3 z, Vec3 c, int n, double[] __p, ChainCtx ctx)
// or Quat equivalent. The returned Vec3/Quat is stored in ctx under OutputName
// so later steps can reference it as `ctx.Get("name")`.

namespace FracturingFog.Models
{
    public sealed class UserBulbChainStep
    {
        public string OutputName { get; set; } = "out";
        public string Source { get; set; } = "return z * z + c;";

        public UserBulbChainStep Clone() => new() { OutputName = OutputName, Source = Source };
    }
}

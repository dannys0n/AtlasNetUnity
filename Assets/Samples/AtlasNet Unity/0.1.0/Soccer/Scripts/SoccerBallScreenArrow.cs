using UnityEngine;
using UnityEngine.UI;

namespace AtlasNet.Samples.Soccer
{
    // A vector HUD arrow, avoiding a texture or any additional network state.
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class SoccerBallScreenArrow : MaskableGraphic
    {
        protected override void OnPopulateMesh(VertexHelper helper)
        {
            helper.Clear();var r=GetPixelAdjustedRect();float x=r.center.x;float shoulder=r.yMin+r.height*.43f;
            helper.AddVert(new Vector3(r.xMin,shoulder),color,Vector2.zero);
            helper.AddVert(new Vector3(x,r.yMax),color,Vector2.zero);
            helper.AddVert(new Vector3(r.xMax,shoulder),color,Vector2.zero);
            helper.AddVert(new Vector3(x-r.width*.16f,r.yMin),color,Vector2.zero);
            helper.AddVert(new Vector3(x+r.width*.16f,r.yMin),color,Vector2.zero);
            helper.AddVert(new Vector3(x-r.width*.16f,shoulder),color,Vector2.zero);
            helper.AddVert(new Vector3(x+r.width*.16f,shoulder),color,Vector2.zero);
            helper.AddTriangle(0,1,2);helper.AddTriangle(3,5,6);helper.AddTriangle(3,6,4);
        }
    }
}

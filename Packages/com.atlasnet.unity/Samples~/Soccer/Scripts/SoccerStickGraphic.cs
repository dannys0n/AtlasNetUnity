using UnityEngine;
using UnityEngine.UI;

namespace AtlasNet.Samples.Soccer
{
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class SoccerStickGraphic : MaskableGraphic, ICanvasRaycastFilter
    {
        protected override void OnPopulateMesh(VertexHelper helper)
        {
            helper.Clear();var rect=GetPixelAdjustedRect();Vector2 center=rect.center;float radius=Mathf.Min(rect.width,rect.height)*.5f;
            const int segments=64;helper.AddVert(center,color,Vector2.zero);
            for(int i=0;i<segments;i++)
            {float angle=i*Mathf.PI*2/segments;helper.AddVert(center+new Vector2(Mathf.Cos(angle),Mathf.Sin(angle))*radius,color,Vector2.zero);}
            for(int i=0;i<segments;i++)helper.AddTriangle(0,1+i,1+(i+1)%segments);
        }
        public bool IsRaycastLocationValid(Vector2 point,Camera eventCamera)
        {
            if(!RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform,point,eventCamera,out var local))return false;
            float radius=Mathf.Min(rectTransform.rect.width,rectTransform.rect.height)*.5f;
            return (local-rectTransform.rect.center).sqrMagnitude<=radius*radius;
        }
    }
}

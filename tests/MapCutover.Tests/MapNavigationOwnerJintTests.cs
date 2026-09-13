using System.Text.Json;
using Jint;
using Xunit;

namespace MapCutover.Tests;

public sealed class MapNavigationOwnerJintTests
{
    [Fact]
    public void Owner_KeyChangeRestoresIndependentAThenBThenAState()
    {
        var engine = CreateEngine();
        engine.Execute("owner = window.mapNavigation.create('map'); owner.toggle('image'); owner.toggle('linked'); listeners.wheel(wheel(-120, 50, 50)); aView = svg.attrs.viewBox;");
        engine.Execute("svg.dataset.mapKey='B'; owner.reconcile(); bDefault=owner.snapshot(); owner.toggle('unlinked'); listeners.wheel(wheel(-120, 25, 25)); bView=svg.attrs.viewBox;");
        engine.Execute("svg.dataset.mapKey='A'; owner.reconcile(); restored=owner.snapshot(); restoredView=svg.attrs.viewBox;");
        var restored = JsonDocument.Parse(engine.Evaluate("JSON.stringify(restored)").AsString()).RootElement;
        Assert.False(restored.GetProperty("image").GetBoolean());
        Assert.True(restored.GetProperty("linked").GetBoolean());
        Assert.True(restored.GetProperty("unlinked").GetBoolean());
        Assert.Equal(engine.Evaluate("aView").AsString(), engine.Evaluate("restoredView").AsString());
        Assert.NotEqual(engine.Evaluate("aView").AsString(), engine.Evaluate("bView").AsString());
    }

    [Fact]
    public void ExplicitControlsToggleResetAccessibilityPanClickAndDisposeWithoutDotNet()
    {
        var engine = CreateEngine();
        engine.Execute("owner=window.mapNavigation.create('map'); controls[0].listeners.click(clickEvent()); imageAfter=owner.snapshot().image; imageLabel=controls[0].attrs['aria-label']; imagePressed=controls[0].attrs['aria-pressed'];");
        Assert.False(engine.Evaluate("imageAfter").AsBoolean());
        Assert.Equal("show map image", engine.Evaluate("imageLabel").AsString());
        Assert.Equal("false", engine.Evaluate("imagePressed").AsString());
        engine.Execute("listeners.wheel(wheel(-120,50,50)); zoomed=svg.attrs.viewBox; listeners.pointerdown(pointer(0,20,20,7)); listeners.pointermove(pointer(0,40,40,7)); listeners.pointerup(pointer(0,40,40,7)); e=clickEvent(); listeners.click(e); suppressed=e.prevented&&e.stopped; controls[3].listeners.click(clickEvent()); resetView=svg.attrs.viewBox;");
        Assert.True(engine.Evaluate("suppressed").AsBoolean());
        Assert.NotEqual(engine.Evaluate("zoomed").AsString(), engine.Evaluate("resetView").AsString());
        Assert.Equal("-3 -3 106 106", engine.Evaluate("resetView").AsString());
        engine.Execute("beforeDispose=svg.attrs.viewBox; owner.dispose(); listeners.wheel(wheel(-120,50,50)); afterDispose=svg.attrs.viewBox;");
        Assert.Equal(engine.Evaluate("beforeDispose").AsString(), engine.Evaluate("afterDispose").AsString());
        Assert.Equal(0, engine.Evaluate("dotNetCalls").AsNumber());
    }

    private static Engine CreateEngine()
    {
        var script = File.ReadAllText(Path.Combine(RepositoryRoot(), "Silksong Rando Logic Manager", "Silksong Rando Logic Manager", "wwwroot", "map-navigation.js"));
        script = script[..script.IndexOf("window.mapOverlayCalibration", StringComparison.Ordinal)];
        const string harness = """
            var dotNetCalls=0, store={}, listeners={};
            var sessionStorage={getItem:k=>store[k]===undefined?null:store[k],setItem:(k,v)=>store[k]=v};
            class AbortController{constructor(){this.signal={aborted:false};}abort(){this.signal.aborted=true;}}
            function classes(initial){var values={};(initial||[]).forEach(x=>values[x]=true);return{toggle:(x,on)=>values[x]=on,contains:x=>!!values[x]};}
            function control(command,compact){return{dataset:{mapCommand:command},attrs:{},classList:classes(compact?['map-image-toggle-compact']:[]),listeners:{},setAttribute:function(k,v){this.attrs[k]=String(v);},addEventListener:function(k,v){this.listeners[k]=v;}};}
            var controls=[control('image',true),control('linked'),control('unlinked'),control('reset')];
            var linked={classList:classes(),style:{}},unlinked={classList:classes(),style:{}},image={style:{}};
            var root={querySelectorAll:s=>controls};
            var svg={dataset:{mapKey:'A',baseViewbox:'0 0 100 100',initialViewbox:'0 0 100 100',mapViewVersion:'3',hasOverlay:'true'},attrs:{viewBox:'0 0 100 100'},isConnected:true,clientHeight:100,clientWidth:100,classList:classes(),
              closest:s=>root,querySelectorAll:function(s){if(s.indexOf('linked')>=0&&s.indexOf('unlinked')<0)return[linked];if(s.indexOf('unlinked')>=0)return[unlinked];if(s==='.map-overlay-image')return[image];return[];},
              setAttribute:function(k,v){this.attrs[k]=String(v);},getAttribute:function(k){return this.attrs[k];},addEventListener:function(k,v){listeners[k]=v;},
              createSVGPoint:function(){return{x:0,y:0,matrixTransform:function(){return{x:this.x,y:this.y};}};},getScreenCTM:()=>({inverse:()=>({})}),setPointerCapture:()=>{},hasPointerCapture:()=>true,releasePointerCapture:()=>{}};
            var document={getElementById:id=>id==='map'?svg:null}; var window={};
            function wheel(delta,x,y){return{deltaY:delta,deltaMode:0,clientX:x,clientY:y,preventDefault:function(){this.prevented=true;}};}
            function pointer(button,x,y,id){return{button:button,clientX:x,clientY:y,pointerId:id};}
            function clickEvent(){return{prevented:false,stopped:false,preventDefault:function(){this.prevented=true;},stopImmediatePropagation:function(){this.stopped=true;}};}
            """;
        return new Engine().Execute(harness).Execute(script);
    }

    private static string RepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, "Silksong Rando Logic Manager"))) return current.FullName;
        throw new DirectoryNotFoundException();
    }
}

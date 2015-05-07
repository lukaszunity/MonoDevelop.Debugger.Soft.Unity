rm -rf build-mpack
mkdir build-mpack
xbuild /p:Configuration=Release /t:Rebuild

cd build-mpack

mono "/Applications/Xamarin Studio.app/Contents/Resources/lib/monodevelop/bin/mdtool.exe" setup pack ../obj/Release/MonoDevelop.Debugger.Soft.Unity.dll ../obj/Release/UnityUtilities.dll

cd ..
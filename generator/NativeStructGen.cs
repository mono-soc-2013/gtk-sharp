using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace GtkSharp.Generation
{
	public class NativeStructGen : HandleBase
	{
		IList<StructField> fields = new List<StructField> ();
		bool need_read_native = false;
		string native_struct_name;

		public NativeStructGen (XmlElement ns, XmlElement elem) : base (ns, elem)
		{
			native_struct_name = Name + "Impl";

			foreach (XmlNode node in elem.ChildNodes) {

				if (!(node is XmlElement)) continue;
				XmlElement member = (XmlElement) node;

				switch (node.Name) {
				case "field":
					fields.Add (new StructField (member, this));
					break;

				default:
					if (!IsNodeNameHandled (node.Name))
						Console.WriteLine ("Unexpected node " + node.Name + " in " + CName);
					break;
				}
			}
		}

		public override string MarshalType {
			get {
				return "IntPtr";
			}
		}

		public override string AssignToName {
			get { return "Handle"; }
		}

		public override string CallByName ()
		{
			return "Handle";
		}

		public override string CallByName (string var)
		{
			return String.Format ("{0} == null ? IntPtr.Zero : {0}.{1}", var, "Handle");
		}

		public override string FromNative (string var, bool owned)
		{
			return "new " + QualifiedName + "( " + var + " )";
		}

		public override void Generate (GenerationInfo gen_info)
		{
			bool need_close = false;
			if (gen_info.Writer == null) {
				gen_info.Writer = gen_info.OpenStream (Name);
				need_close = true;
			}

			StreamWriter sw = gen_info.Writer;
			
			sw.WriteLine ("namespace " + NS + " {");
			sw.WriteLine ();
			sw.WriteLine ("\tusing System;");
			sw.WriteLine ("\tusing System.Collections;");
			sw.WriteLine ("\tusing System.Collections.Generic;");
			sw.WriteLine ("\tusing System.Runtime.InteropServices;");
			sw.WriteLine ();

			sw.WriteLine ("#region Autogenerated code");
			if (IsDeprecated)
				sw.WriteLine ("\t[Obsolete]");
			string access = IsInternal ? "internal" : "public";
			sw.WriteLine ("\t" + access + " partial class {0} : IEquatable<{0}> {{", Name);
			sw.WriteLine ();

			need_read_native = false;
			GenNativeStruct (gen_info);
			GenUpdate (gen_info);
			GenFields (gen_info);
			sw.WriteLine ();
			GenCtors (gen_info);
			GenMethods (gen_info, null, this);
			if (need_read_native)
				GenUpdate (gen_info);
			GenEqualsAndHash (sw);

			if (!need_close)
				return;

			sw.WriteLine ("#endregion");

			sw.WriteLine ("\t}");
			sw.WriteLine ("}");
			sw.Close ();
			gen_info.Writer = null;
		}

		private void GenNativeStruct (GenerationInfo gen_info)
		{
			StreamWriter sw = gen_info.Writer;

			sw.WriteLine ("\t\t[StructLayout(LayoutKind.Sequential)]");
			sw.WriteLine ("\t\tprivate struct {0} {{", native_struct_name);
			foreach (StructField field in fields) {
				field.Generate (gen_info, "\t\t\t");
			}
			sw.WriteLine ("\t\t}");
			sw.WriteLine ();
		}

		private void GenUpdate (GenerationInfo gen_info)
		{
			StreamWriter sw = gen_info.Writer;

			sw.WriteLine ("\t\tprivate void Update ()", QualifiedName);
			sw.WriteLine ("\t\t{");
			sw.WriteLine ("\t\t\tif (Handle != IntPtr.Zero)");
			sw.WriteLine ("\t\t\t\tthis.managed_struct = ({0})Marshal.PtrToStructure(this.Handle, typeof({0}));", native_struct_name);
			sw.WriteLine ("\t\t}");
			sw.WriteLine ();
		}

		protected override void GenCtors (GenerationInfo gen_info)
		{
			StreamWriter sw = gen_info.Writer;

			sw.WriteLine ("\t\tpublic {0} (IntPtr raw)", Name);
			sw.WriteLine ("\t\t{");
			sw.WriteLine ("\t\t\tthis.Handle = raw;");
			sw.WriteLine ("\t\t}");
			sw.WriteLine ();

			base.GenCtors (gen_info);
		}

		protected new void GenFields (GenerationInfo gen_info)
		{
			StreamWriter sw = gen_info.Writer;
			sw.WriteLine ("\t\tprivate IntPtr Raw;");
			sw.WriteLine ("\t\tpublic IntPtr Handle {");
			sw.WriteLine ("\t\t\tget { return Raw; }");
			sw.WriteLine ("\t\t\tset { Raw = value; Update ();}");
			sw.WriteLine ("\t\t}");
			sw.WriteLine ("\t\tprivate {0} managed_struct;", native_struct_name);
			sw.WriteLine ();
			foreach (StructField field in fields) {
				if(!field.Visible)
					continue;
				sw.WriteLine ("\t\tpublic {0} {1} {{", SymbolTable.Table.GetCSType (field.CType), field.StudlyName);
				sw.WriteLine ("\t\t\tget {{ Update(); return {0}.{1}; }}", "managed_struct", field.StudlyName);
				if (!(SymbolTable.Table [field.CType] is CallbackGen))
					sw.WriteLine ("\t\t\tset {{ Update(); {0}.{1} = value;  Marshal.StructureToPtr({0}, this.Handle, false); }}" , "managed_struct", field.StudlyName);
				sw.WriteLine ("\t\t}");
			}
		}

		protected void GenEqualsAndHash (StreamWriter sw)
		{
			int bitfields = 0;
			bool need_field = true;
			StringBuilder hashcode = new StringBuilder ();
			StringBuilder equals = new StringBuilder ();

			sw.WriteLine ("\t\tpublic bool Equals ({0} other)", Name);
			sw.WriteLine ("\t\t{");
			hashcode.Append("this.GetType().FullName.GetHashCode()");
			equals.Append("true");

			foreach (StructField field in fields) {
				if(field.IsPadding || !field.Visible)
					continue;
				if (field.IsBitfield) {
					if (need_field) {
						equals.Append (" && _bitfield");
						equals.Append (bitfields);
						equals.Append (".Equals (other._bitfield");
						equals.Append (bitfields);
						equals.Append (")");
						hashcode.Append (" ^ ");
						hashcode.Append ("_bitfield");
						hashcode.Append (bitfields++);
						hashcode.Append (".GetHashCode ()");
						need_field = false;
					}
				} else {
					need_field = true;
					equals.Append (" && ");
					equals.Append (field.EqualityName);
					equals.Append (".Equals (other.");
					equals.Append (field.EqualityName);
					equals.Append (")");
					hashcode.Append (" ^ ");
					hashcode.Append (field.EqualityName);
					hashcode.Append (".GetHashCode ()");
				}
			}
			sw.WriteLine ("\t\t\treturn {0};", equals.ToString());
			sw.WriteLine ("\t\t}");
			sw.WriteLine ();
			sw.WriteLine ("\t\tpublic override bool Equals (object other)");
			sw.WriteLine ("\t\t{");
			sw.WriteLine ("\t\t\treturn other is {0} && Equals (({0}) other);", Name);
			sw.WriteLine ("\t\t}");
			sw.WriteLine ();
			if (Elem.GetAttribute ("nohash") == "true")
				return;
			sw.WriteLine ("\t\tpublic override int GetHashCode ()");
			sw.WriteLine ("\t\t{");
			sw.WriteLine ("\t\t\treturn {0};", hashcode.ToString ());
			sw.WriteLine ("\t\t}");
			sw.WriteLine ();

		}

		public override void Prepare (StreamWriter sw, string indent)
		{
			sw.WriteLine(indent + "Update ();");
		}
	}
}

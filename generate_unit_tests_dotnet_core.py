# Python script to generate basic unit tests for .NET Core C# files
# This script uses Tree-sitter to parse C# source files, extract classes, constructors, and methods,
# and generate skeleton xUnit tests for each method. It aims for basic line coverage
# by calling each method with default/dummy arguments. Achieving 100% coverage
# (including branches, exceptions, etc.) requires manual refinement or advanced tools.
# 
# Supports xUnit tests with [Fact] attributes.
# Now supports async methods: detects 'async' modifier, generates async test methods,
# and uses await for calls. Handles Task and Task<T> return types appropriately.
# 
# Simplified dummy value inference using extensible regex patterns for primitives.
# Handles nested generic types recursively (e.g., Task<List<List<int>>> → Task.FromResult(new List<List<int>>())).
# Handles array types (e.g., int[] → new int[] {0}, List<string>[] → new List<string>[] { new List<string>() },
#   object[] → new object[] {} if no dummy).
# 
# Integrates Moq for dependency resolution: Detects constructors, mocks interface parameters (starting with 'I'),
#   injects mocks into class instantiation. Non-interface params use dummies. Assumes Moq NuGet package installed
#   in test project (run: dotnet add package Moq). 
# Now includes example Moq setups for each mock in the Arrange section (e.g., .Setup(x => x.Method(It.IsAny<T>())).Returns(dummy)).
#   These are placeholders—refine based on actual interface methods and test scenarios (e.g., per-test setups).
# 
# Prerequisites:
# - pip install tree-sitter tree-sitter-c-sharp
# - A .NET Core project with xUnit test project set up (e.g., via 'dotnet new xunit')
# - Add Moq: dotnet add [test-project] package Moq
# - Place this script in the root of your solution or adjust paths accordingly.
# 
# Usage:
# python generate_tests.py /path/to/your/project/src /path/to/your/test/project
# 
# Limitations:
# - Handles primary constructor (greediest by param count); skips complex signatures.
# - Mocks only interfaces (I*); concrete deps use dummies or null—refine manually.
# - Setup examples are generic (assumes common method like 'GetX' returning dummy)—customize for real APIs.
# - Dummy args for method params: null for unknown objects (may need mocks/values).
# - Assumes public classes/methods; skips constructors with test-like names.
# - Tests placed in new file per source file, e.g., MyClassTests.cs
# - Run 'dotnet test' after generation to check coverage with coverlet or similar.

import os
import sys
import argparse
import re
from pathlib import Path
from tree_sitter import Language, Parser, Node
from tree_sitter_c_sharp import language as csharp_lang  # From tree-sitter-c-sharp package

def setup_parser():
    """Set up Tree-sitter parser for C#."""
    LANGUAGE = csharp_lang()
    parser = Parser()
    parser.set_language(LANGUAGE)
    return parser, LANGUAGE

def get_query(language):
    """Tree-sitter query to extract classes, constructors, and methods."""
    query_str = '''
    (class_declaration
      name: (identifier) @class.name
      body: (declaration_list
        (method_declaration
          name: (identifier) @method.name
          (#match? @method.name "^(?!Test|Arrange|Act|Assert).*")
        ) @method
        (constructor_declaration
          (#match? @constructor.name "^(?!Test|Arrange|Act|Assert).*")
        ) @constructor
      )
    )
    '''
    return language.query(query_str)

def parse_generic_type(type_str):
    """Recursively parse generic types: returns (base, [(subbase, [subsub...]), ...]) or (type_name, []) if non-generic."""
    type_str = type_str.replace(' ', '')  # Remove spaces for parsing
    lt_pos = type_str.find('<')
    if lt_pos == -1:
        return type_str, []
    base = type_str[:lt_pos]
    args_str = type_str[lt_pos+1:-1]  # Remove < >
    # Parse args with depth for nesting
    args = []
    current_arg = ''
    depth = 0
    for char in args_str:
        if char == '<':
            depth += 1
        elif char == '>':
            depth -= 1
        elif char == ',' and depth == 0:
            args.append(current_arg.strip())
            current_arg = ''
            continue
        current_arg += char
    if current_arg:
        args.append(current_arg.strip())
    # Recurse on args
    parsed_args = [parse_generic_type(arg) for arg in args]
    return base, parsed_args

def generic_to_string(base, args):
    """Reconstruct full type string from parsed generic structure."""
    if not args:
        return base
    arg_strs = [generic_to_string(a[0], a[1]) for a in args]
    return f"{base}<{', '.join(arg_strs)}>"

def infer_dummy_value(type_name):
    """Infer a dummy value based on type inference, handling arrays and nested generics."""
    type_str = type_name.strip().replace(' ', '')
    # Handle arrays first
    if type_str.endswith('[]'):
        elem_str = type_str[:-2]
        elem_parsed = parse_generic_type(elem_str)
        elem_type_str = generic_to_string(*elem_parsed)
        elem_dummy = infer_dummy_value(elem_type_str)
        if elem_dummy == 'null':
            return f'new {type_str} {{ }}'
        else:
            return f'new {type_str} {{ {elem_dummy} }}'
    # Handle generics
    base, generics = parse_generic_type(type_str)
    type_lower = base.lower()
    # Known generic bases
    if generics:
        full_type = generic_to_string(base, generics)
        if type_lower in ['list', 'ilist', 'icollection', 'ienumerable']:
            return f'new {full_type}()'
        elif type_lower == 'dictionary':
            if len(generics) >= 2:
                return f'new {full_type}()'
            else:
                return 'null'
        elif type_lower == 'task':
            if len(generics) == 1:
                inner_parsed = generics[0]
                inner_type_str = generic_to_string(*inner_parsed)
                inner_dummy = infer_dummy_value(inner_type_str)
                return f'Task.FromResult({inner_dummy})'
            else:
                return 'Task.CompletedTask'
        # Unrecognized generics fall through to 'null'
    # Non-generic patterns
    dummy_patterns = [
        (r'^int$', '0'),
        (r'^long$', '0L'),
        (r'^float$', '0.0f'),
        (r'^double$', '0.0'),
        (r'^bool$', 'false'),
        (r'^string$', '""'),
        (r'^decimal$', '0m'),
        (r'^char$', "'a'"),
        (r'^byte$', '0'),
        (r'^short$', '0'),
        (r'^sbyte$', '0'),
        (r'^ushort$', '0'),
        (r'^uint$', '0u'),
        (r'^ulong$', '0UL'),
        (r'^datetime$', 'DateTime.Now'),
        # Add more as needed
    ]
    for pattern, value in dummy_patterns:
        if re.search(pattern, type_lower):
            return value
    return 'null'  # Default for unknown objects

def extract_params(decl_node):
    """Extract parameter types from declaration node."""
    params = []
    param_node = decl_node.child_by_field_name('parameters')
    if param_node:
        for child in param_node.named_children:
            if child.type == 'parameter':
                type_node = child.child_by_field_name('type')
                if type_node:
                    param_type = type_node.text.decode('utf8').strip()
                    params.append(param_type)
    return params

def extract_methods(code_bytes, parser, query):
    """Parse code and extract class/method/ctor info."""
    tree = parser.parse(code_bytes)
    captures = query.captures(tree.root_node)
    
    classes = {}
    current_class = None
    for capture in captures:
        node, tag = capture
        if tag == 'class.name':
            current_class = node.text.decode('utf8').strip()
            classes[current_class] = {'methods': [], 'constructors': []}
        elif tag == 'method.name' and current_class:
            decl_node = node.parent
            method_name = node.text.decode('utf8').strip()
            
            params = extract_params(decl_node)
            
            return_node = decl_node.child_by_field_name('type')
            return_type = return_node.text.decode('utf8').strip() if return_node else 'void'
            
            is_async = False
            modifiers_node = decl_node.child_by_field_name('modifiers')
            if modifiers_node:
                for mod_child in modifiers_node.named_children:
                    if mod_child.type == 'modifier':
                        mod_name_node = mod_child.child_by_field_name('name') or mod_child.named_child(0)
                        if mod_name_node and mod_name_node.text.decode('utf8').strip() == 'async':
                            is_async = True
                            break
            
            classes[current_class]['methods'].append({
                'name': method_name,
                'params': params,
                'return_type': return_type,
                'is_async': is_async
            })
        elif tag == 'constructor' and current_class:
            decl_node = node
            params = extract_params(decl_node)
            classes[current_class]['constructors'].append({'params': params})
    return classes

def compute_arrange(class_name, constructors):
    """Generate Arrange section with Moq for dependencies, including example setups."""
    arrange = "            // Arrange\n"
    if constructors:
        ctor = max(constructors, key=lambda c: len(c['params']))
        params = ctor['params']
        args_list = []
        mock_decls = []
        setup_lines = []
        for i, param_type in enumerate(params):
            if param_type.startswith('I'):
                mock_var = f"mock{i}"
                mock_decls.append(f"            var {mock_var} = new Mock<{param_type}>();")
                # Example setup: Assume a common method like 'GetX' returning a dummy of inferred type (placeholder)
                # In practice, replace 'GetX' with actual method and adjust return type
                dummy_ret = infer_dummy_value(param_type)  # Reuse for setup return; adjust as needed
                setup = f"            {mock_var}.Setup(x => x.Get{param_type[1:]}Default(It.IsAny<object>())).Returns({dummy_ret});  // TODO: Customize setup for {param_type}"
                setup_lines.append(setup)
                args_list.append(f"{mock_var}.Object")
            else:
                dummy = infer_dummy_value(param_type)
                args_list.append(dummy)
        if mock_decls:
            arrange += '\n'.join(mock_decls) + '\n'
            if setup_lines:
                arrange += '\n'.join(setup_lines) + '\n'
        ctor_args = ', '.join(args_list)
        if ctor_args:
            arrange += f"            var target = new {class_name}({ctor_args});\n"
        else:
            arrange += f"            var target = new {class_name}();\n"
    else:
        arrange += f"            var target = new {class_name}();\n"
    return arrange

def generate_test_class(class_name, methods, constructors, namespace='YourProject.Tests'):
    """Generate xUnit test class code."""
    arrange_str = compute_arrange(class_name, constructors)
    
    test_class = f"""using Xunit;
using Moq;
using System.Threading.Tasks;
using {namespace.replace('.Tests', '')};  // Adjust namespace as needed

namespace {namespace}
{{
    public class {class_name}Tests
    {{
"""
    for method in methods:
        method_name = method['name']
        params = method['params']
        args = ', '.join(infer_dummy_value(p) for p in params)
        call_base = f"target.{method_name}({args})" if params else f"target.{method_name}()"
        
        is_async = method['is_async']
        return_type = method['return_type']
        ret_base, ret_generics = parse_generic_type(return_type)
        is_task_void = ret_base.lower() == 'task' and len(ret_generics) == 0
        has_return_value = return_type != 'void' and not is_task_void
        
        # Test method signature
        sig = f"        [Fact]\n"
        if is_async:
            sig += f"        public async Task {method_name}_ShouldWorkWithDefaults()\n        {{\n"
        else:
            sig += f"        public void {method_name}_ShouldWorkWithDefaults()\n        {{\n"
        
        # Act section
        act = "            // Act\n"
        if is_async:
            call_str = f"await {call_base}"
        else:
            call_str = call_base
        
        if has_return_value:
            act += f"            var actual = {call_str};\n"
            assertion = f"            Assert.NotNull(actual);  // TODO: Refine expected value"
        else:
            act += f"            {call_str};\n"
            assertion = f"            // TODO: Add assertions for side effects"
        
        test_method = f"{sig}{arrange_str}{act}\n\n            // Assert\n{assertion}\n        }}\n"
        
        test_class += test_method
    
    test_class += "    }\n}"
    return test_class

def main(src_dir, test_dir):
    parser, language = setup_parser()
    query = get_query(language)
    
    for root, _, files in os.walk(src_dir):
        for file in files:
            if file.endswith('.cs') and not file.startswith('Program') and not file.endswith('Tests.cs'):
                file_path = os.path.join(root, file)
                with open(file_path, 'rb') as f:
                    code_bytes = f.read()
                
                classes = extract_methods(code_bytes, parser, query)
                
                relative_path = os.path.relpath(file_path, src_dir)
                test_file_name = Path(relative_path).with_suffix('Tests.cs').with_stem(Path(file).stem + 'Tests')
                test_file_path = os.path.join(test_dir, test_file_name)
                
                with open(test_file_path, 'w') as tf:
                    for class_name, class_info in classes.items():
                        methods = class_info['methods']
                        constructors = class_info['constructors']
                        if methods:  # Only if has methods
                            test_code = generate_test_class(class_name, methods, constructors)
                            tf.write(test_code)
                            print(f"Generated tests for {class_name} in {test_file_path}")
    
    print("Test generation complete. Run 'dotnet build' and 'dotnet test' to verify.")

if __name__ == '__main__':
    parser_arg = argparse.ArgumentParser(description='Generate xUnit tests for .NET Core C# files.')
    parser_arg.add_argument('src_dir', help='Path to source directory (e.g., src/)')
    parser_arg.add_argument('test_dir', help='Path to test project directory (e.g., tests/MyProject.Tests/)')
    args = parser_arg.parse_args()
    main(args.src_dir, args.test_dir)
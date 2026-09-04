# Senior Software Engineer Exercise

## Business Scenario

Your team is developing software for an industrial manufacturing machine used in automated production environments.

The machine executes a production workflow that consists of multiple processing stages (stage_1, stage_2, stage_3). Stages may be executed in parallel based on the current value of the sensors. There will be a sensor module that updates value of each sensor every 100ms. 

### List of sensors include:

- Temperature sensors
- Pressure sensors 

### List of resources:
- Resource R_A
- Resource R_B
- Resource R_C

### Stage Map:
 - stage_1 {R_A, R_B}
 - stage_2 {R_C, R_B}
 - stage_3 {R_A, R_C}

If Temperature is more than 10.0 and Pressure is less than 100 then stage_1 and stage_2 shall be executed.

If Temperature is more than 5.0 and Pressure is less than 50 then stage_3 and stage_2 shall be executed.

If Temperature is more than 20.0 and Pressure is less than 100 then stage_1 and stage_3 shall be executed.

##Note:
A resource will have busy, idle, and error state. You can fake these resources by creating a simple class with three internal states.   

Preventing concurrency-related issues is an important aspect of this exercise. Therefore, we strongly encourage candidates to familiarize themselves with topic (see the accompanying material) and consider concurrency challenges when designing their solution. The proposed design should demonstrate an understanding of concurrent processing and include mechanisms to address at least two potential concurrency-related problems. A successful candidate will be able to present at least one deadlock and one non-deadlock (Order-Violation Bugs, Atomicity-Violation Bugs) problems.



## Note that, the manufacturing process is expected to evolve over time:

- New sensors may be introduced.
- Existing sensors may be replaced.

The software system should therefore be designed with flexibility and maintainability in mind.


## Part 1 – Conceptual Design

Before implementation, design the solution at a conceptual level.

Describe:

### Architecture

- Main system components
- Component responsibilities
- Communication patterns

### Design Considerations

Discuss your approach to:

- Extensibility
- Maintainability
- Testability
- Reliability

### Diagrams

Our preferred formats are:

- Markdown (`.md`)
- Mermaid diagrams

However, candidates are free to use any tools they consider appropriate.


## Part 2 – Implementation

Implement the solution based on your design.

### Required Capabilities

At minimum, the solution should demonstrate:

- Sensor registration or management
- Reception of sensor measurements
- Support for multiple independent consumers (i.e., processes) of the same sensor data 
- Properly manage your three resources (A, B, C) among different processes

### Technology Choices

You are free to choose:

- Programming language
- Frameworks
- Storage technologies
- Architectural style

Document the decisions you make and any assumptions you consider important.


## Submission Requirements

The completed exercise must be submitted as a GitHub repository.

The repository should contain:

- Source code
- Design documentation
- Architecture diagrams
- Tests
- Build and execution instructions
- Any additional supporting material

Please provide a link to the repository containing your solution at least 24h before your video interview.

## Evaluation Criteria

The solution will be evaluated based on:

- Architecture and design quality
- Correctness
- Simplicity and clarity
- Extensibility
- Maintainability
- Testability
- Documentation quality
- Commit history and development process
- Ability to explain design decisions and trade-offs

The goal is to demonstrate engineering judgment, not simply produce a working application.

# Submission Requirements

The completed exercise must be delivered as a GitHub repository.

Please provide:

- A link to the GitHub repository.
- All source code.
- Architecture and design documentation.
- Diagrams and other design artifacts.
- Build and execution instructions.
- Automated tests.

All deliverables must be stored within the repository itself. External documents, files, or links should be avoided unless absolutely necessary.



# Use of AI and External Resources

Candidates are free to use any available resources, including:

- GitHub Copilot
- Large Language Models (LLMs)
- AI-assisted development tools
- Online documentation
- Open-source libraries and frameworks

The use of such tools is not prohibited and will not negatively impact the evaluation.

However, during the review process, candidates should be prepared to discuss and explain:

- Architectural decisions
- Design trade-offs
- Domain modeling choices
- Implementation details
- Testing strategy
- Technology selections

The expectation is that candidates fully understand the solution they submit.

If a candidate is unable to adequately explain significant parts of the design or implementation, it may be concluded that:

- The work was not primarily produced by the candidate; or
- The solution relies excessively on generative AI without sufficient understanding.

In such cases, the application may be rejected.

We value engineering judgment and understanding more than the volume of code produced.
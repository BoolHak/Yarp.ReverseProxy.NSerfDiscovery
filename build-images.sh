#!/bin/bash

echo "Building Docker images for integration tests..."

# Build Gateway image
echo "Building test-gateway image..."
docker build -f TestGateway/Dockerfile -t test-gateway:latest .

# Build Service image
echo "Building test-service image..."
docker build -f TestService/Dockerfile -t test-service:latest .

echo ""
echo "Docker images built successfully!"
echo "Images:"
docker images | grep "test-"
